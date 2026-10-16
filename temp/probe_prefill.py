#!/usr/bin/env python3
# Copyright 2026 Andreas Jung
# SPDX-License-Identifier: GPL-3.0-only
"""
Probe an OpenAI-compatible endpoint (llama-swap / llama-server) for the
assistant-prefill behaviour the "rethink" feature depends on.

v2 - supersedes the first pass, which got three verdicts wrong:

  * llama.cpp ECHOES the prefill back at the head of the stream, so a naive
    "does the output start with the expected continuation?" check always
    fails. Every verdict here strips the echoed prefix first.
  * the old trap ended on a bare "P", which a model can legitimately
    continue as "Parizs"/"Prague"/... The trap is now a fixed NATO word
    sequence with exactly one valid continuation.

Tests:
  1. content-prefill        - trailing assistant `content` continues?
  2. reasoning-closed       - `reasoning_content` + `content:""`:
                              does the think block close and answer directly?
  3. reasoning-open         - `reasoning_content` + `content:""` where the
                              trace is mid-thought: continue thinking, or close?
  4. inline-think-open      - unclosed `<think>` inside `content`: the shape
                              that can leave the block open.
  5. content-null           - is `content:null` accepted (vs the 400 that
                              omitting the key produces)?
  6. no-think-kwargs        - chat_template_kwargs.enable_thinking=false.

stdlib only.

Usage:
    python probe_prefill.py --model <id>
    python probe_prefill.py --list
    python probe_prefill.py --model <id> --test 2,3 --verbose
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

# -- the trap -----------------------------------------------------------------
# A fixed sequence has exactly one valid continuation, so "did it continue?"
# has no linguistic wiggle room (unlike a half-spelled proper noun).

SEQ_FULL   = "ALPHA BRAVO CHARLIE DELTA ECHO FOXTROT GOLF"
SEQ_PREFIX = "ALPHA BRAVO CHARLIE DEL"     # mid-word on purpose
SEQ_TAIL   = "TA"                          # what a continuation must emit next

SEQ_TASK = ("Output exactly this sequence and nothing else, no commentary:\n"
            + SEQ_FULL)


class Colors:
    def __init__(self, enabled):
        self.dim   = "\033[2m"  if enabled else ""
        self.think = "\033[36m" if enabled else ""
        self.main  = "\033[32m" if enabled else ""
        self.warn  = "\033[33m" if enabled else ""
        self.fail  = "\033[31m" if enabled else ""
        self.ok    = "\033[92m" if enabled else ""
        self.bold  = "\033[1m"  if enabled else ""
        self.off   = "\033[0m"  if enabled else ""


# -- wire ---------------------------------------------------------------------

def post_stream(url, body, timeout, C, verbose):
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        url.rstrip("/") + "/v1/chat/completions",
        data=data,
        headers={"Content-Type": "application/json",
                 "Accept": "text/event-stream"},
        method="POST")

    out = {"reasoning": "", "content": "", "reasoning_chunks": 0,
           "content_chunks": 0, "finish": None, "error": None}

    try:
        resp = urllib.request.urlopen(req, timeout=timeout)
    except urllib.error.HTTPError as e:
        payload = e.read().decode("utf-8", "replace")
        out["error"] = "HTTP %d: %s" % (e.code, payload[:600])
        print("  %s!! %s%s" % (C.fail, out["error"], C.off), flush=True)
        return out
    except Exception as e:  # noqa: BLE001
        out["error"] = "transport: %r" % (e,)
        print("  %s!! %s%s" % (C.fail, out["error"], C.off), flush=True)
        return out

    last = None
    t0 = time.time()
    with resp:
        while True:
            raw = resp.readline()
            if not raw:
                break
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            payload = line[5:].strip()
            if payload == "[DONE]":
                break
            if verbose:
                print("\n%s    raw: %s%s" % (C.dim, payload[:400], C.off), flush=True)
            try:
                chunk = json.loads(payload)
            except json.JSONDecodeError:
                continue

            for ch in chunk.get("choices") or []:
                d = ch.get("delta") or {}
                rc = d.get("reasoning_content")
                if rc:
                    if last != "think":
                        print("\n%s  [think] %s" % (C.think, C.off), end="", flush=True)
                        last = "think"
                    print("%s%s%s" % (C.think, rc, C.off), end="", flush=True)
                    out["reasoning"] += rc
                    out["reasoning_chunks"] += 1
                cc = d.get("content")
                if cc:
                    if last != "main":
                        print("\n%s  [main]  %s" % (C.main, C.off), end="", flush=True)
                        last = "main"
                    print("%s%s%s" % (C.main, cc, C.off), end="", flush=True)
                    out["content"] += cc
                    out["content_chunks"] += 1
                if ch.get("finish_reason"):
                    out["finish"] = ch["finish_reason"]

    print("\n%s  (finish=%s, %.1fs)%s"
          % (C.dim, out["finish"], time.time() - t0, C.off), flush=True)
    return out


def list_models(url, timeout):
    req = urllib.request.Request(url.rstrip("/") + "/v1/models", method="GET")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        doc = json.loads(resp.read().decode("utf-8", "replace"))
    return [m.get("id") for m in (doc.get("data") or []) if m.get("id")]


# -- echo-aware continuation check --------------------------------------------

def analyse(text, prefill, expect_tail):
    """
    Returns (echoed, continued, tail).

    llama.cpp replays the prefill at the head of the stream, so the real
    generated part is whatever follows it. A server that ignored the prefill
    produces text that does NOT start with it — in which case the whole
    output is treated as the tail and the continuation check fails honestly.
    """
    echoed = text.startswith(prefill)
    tail = text[len(prefill):] if echoed else text
    continued = tail.lstrip().upper().startswith(expect_tail.upper())
    return echoed, continued, tail


def fmt_tail(tail, n=60):
    t = tail[:n].replace("\n", "\\n")
    return repr(t)


# -- tests --------------------------------------------------------------------

def build_tests(model, max_tokens):
    base = {"model": model, "stream": True, "temperature": 0.0,
            "max_tokens": max_tokens,
            "stream_options": {"include_usage": True}}

    def body(messages, **extra):
        b = dict(base)
        b["messages"] = messages
        b.update(extra)
        return b

    MID_THOUGHT = ("Let me work through this. The sequence I must output is "
                   + SEQ_PREFIX)
    DONE_THOUGHT = ("The user wants the sequence verbatim. I will output "
                    + SEQ_FULL + ". Done.")

    def v_content(r):
        echoed, cont, tail = analyse(r["content"], SEQ_PREFIX, SEQ_TAIL)
        return cont, ("echoed prefill, continued with %s" % fmt_tail(tail)
                      if echoed and cont else
                      "echoed prefill but tail was %s" % fmt_tail(tail) if echoed else
                      "prefill NOT echoed; output %s" % fmt_tail(tail))

    def v_reasoning(r):
        echoed, cont, tail = analyse(r["reasoning"], MID_THOUGHT, SEQ_TAIL)
        if echoed and cont:
            return True, "reasoning continued mid-thought: %s" % fmt_tail(tail)
        if echoed and not tail.strip():
            return False, ("reasoning echoed verbatim and STOPPED; answer went to "
                           "main channel (%s) -> think block was closed"
                           % fmt_tail(r["content"]))
        if echoed:
            return False, "reasoning echoed, then diverged: %s" % fmt_tail(tail)
        return False, "reasoning NOT echoed; got %s" % fmt_tail(r["reasoning"])

    def v_closed(r):
        echoed = r["reasoning"].startswith(DONE_THOUGHT)
        extra  = r["reasoning"][len(DONE_THOUGHT):] if echoed else r["reasoning"]
        answered = bool(r["content"].strip())
        ok = echoed and not extra.strip() and answered
        if ok:
            return True, ("reasoning echoed verbatim, no new thinking, answered "
                          "on main: %s" % fmt_tail(r["content"]))
        if echoed and extra.strip():
            return False, "kept thinking past the prefill: %s" % fmt_tail(extra)
        if not echoed:
            return False, "prefilled reasoning NOT honored: %s" % fmt_tail(r["reasoning"])
        return False, "no answer produced on the main channel"

    def v_inline(r):
        whole = r["reasoning"] + r["content"]
        pre = "<think>\n" + MID_THOUGHT
        echoed, cont, tail = analyse(whole, pre, SEQ_TAIL)
        where = ("reasoning channel" if r["reasoning_chunks"] else "content channel")
        if echoed and cont:
            return True, ("continued the open think block on the %s: %s"
                          % (where, fmt_tail(tail)))
        if echoed:
            return False, "echoed but did not continue: %s" % fmt_tail(tail)
        return False, "prefill NOT echoed: %s" % fmt_tail(whole)

    def v_null(r):
        if r["error"]:
            return False, "rejected: " + r["error"].split("\n")[0][:110]
        echoed, cont, tail = analyse(r["reasoning"], MID_THOUGHT, SEQ_TAIL)
        return True, ("accepted; reasoning %s, continued=%s"
                      % ("echoed" if echoed else "not echoed", cont))

    def v_nothink(r):
        ok = r["reasoning_chunks"] == 0 and "<think>" not in r["content"]
        return ok, "thinking suppressed" if ok else "thinking still emitted"

    return [
        {"id": 1, "name": "content-prefill",
         "why": "Trailing assistant `content` - continue or restart?",
         "expect": 'after the echoed prefill, output must continue "%s..."' % SEQ_TAIL,
         "body": body([{"role": "user", "content": SEQ_TASK},
                       {"role": "assistant", "content": SEQ_PREFIX}]),
         "verdict": v_content},

        {"id": 2, "name": "reasoning-closed",
         "why": "Completed trace + empty content - does it answer directly?",
         "expect": "reasoning echoed verbatim with nothing added; answer on [main]",
         "body": body([{"role": "user", "content": SEQ_TASK},
                       {"role": "assistant",
                        "reasoning_content": DONE_THOUGHT,
                        "content": ""}]),
         "verdict": v_closed},

        {"id": 3, "name": "reasoning-open",
         "why": "Mid-thought trace + empty content - can thinking continue?",
         "expect": 'reasoning continues "%s..."; if it instead stops and answers, '
                   "the block was force-closed" % SEQ_TAIL,
         "body": body([{"role": "user", "content": SEQ_TASK},
                       {"role": "assistant",
                        "reasoning_content": MID_THOUGHT,
                        "content": ""}]),
         "verdict": v_reasoning},

        {"id": 4, "name": "inline-think-open",
         "why": "Unclosed <think> in content - the leave-it-open shape.",
         "expect": 'continues "%s..." with the block still open' % SEQ_TAIL,
         "body": body([{"role": "user", "content": SEQ_TASK},
                       {"role": "assistant",
                        "content": "<think>\n" + MID_THOUGHT}]),
         "verdict": v_inline},

        {"id": 5, "name": "content-null",
         "why": "Is content:null accepted where omitting the key gave a 400?",
         "expect": "no 400; tells us whether reasoning-only prefill is reachable",
         "body": body([{"role": "user", "content": SEQ_TASK},
                       {"role": "assistant",
                        "reasoning_content": MID_THOUGHT,
                        "content": None}]),
         "verdict": v_null},

        {"id": 6, "name": "no-think-kwargs",
         "why": "Keeps the rethink call itself from thinking.",
         "expect": "zero [think] output",
         "body": body([{"role": "user", "content": SEQ_TASK}],
                      chat_template_kwargs={"enable_thinking": False}),
         "verdict": v_nothink},
    ]


# -- main ---------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="http://localhost:8080")
    ap.add_argument("--model")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--test", default="all")
    ap.add_argument("--max-tokens", type=int, default=200)
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--no-color", action="store_true")
    args = ap.parse_args()

    C = Colors(not args.no_color)

    if args.list:
        for m in list_models(args.url, 30):
            print(m)
        return 0

    model = args.model
    if not model:
        ids = list_models(args.url, 30)
        if not ids:
            print("no --model and /v1/models returned nothing", file=sys.stderr)
            return 2
        model = ids[0]
        print("%s(using first discovered model: %s)%s" % (C.dim, model, C.off))

    tests = build_tests(model, args.max_tokens)
    if args.test != "all":
        wanted = {int(x) for x in args.test.split(",")}
        tests = [t for t in tests if t["id"] in wanted]

    print("%s%s -> %s%s" % (C.bold, args.url, model, C.off))
    print("%strap: prefill ends mid-word at %r; a continuation must emit %r%s\n"
          % (C.dim, SEQ_PREFIX[-12:], SEQ_TAIL, C.off))

    results = []
    for t in tests:
        print("%s==== [%d] %s%s" % (C.bold, t["id"], t["name"], C.off))
        print("%s     %s%s" % (C.dim, t["why"], C.off))
        print("%s     expect: %s%s" % (C.dim, t["expect"], C.off))
        if args.verbose:
            print("%s     body: %s%s" % (C.dim, json.dumps(t["body"])[:900], C.off))

        r = post_stream(args.url, t["body"], args.timeout, C, args.verbose)
        if r["error"] and t["id"] != 5:
            results.append((t, False, "request failed: " + r["error"].split("\n")[0][:110]))
            print("  %sFAIL%s  request failed" % (C.fail, C.off))
        else:
            ok, note = t["verdict"](r)
            results.append((t, ok, note))
            print("  %s%s%s  %s" % (C.ok if ok else C.fail,
                                    "PASS" if ok else "FAIL", C.off, note))
        print()

    print("%s%s%s" % (C.bold, "=" * 64, C.off))
    print("%sSUMMARY%s" % (C.bold, C.off))
    for t, ok, note in results:
        col = C.ok if ok else C.fail
        print("  %s%-4s%s [%d] %-20s %s"
              % (col, "PASS" if ok else "FAIL", C.off, t["id"], t["name"], note[:90]))

    by = {t["id"]: ok for t, ok, _ in results}
    print()
    print("%sWhat this means for rethink:%s" % (C.bold, C.off))
    if by.get(1):
        print("  + Assistant prefill CONTINUES (server echoes the prefill first -")
        print("    the client must strip it before appending to the UI bubble).")
    if by.get(2):
        print("  + compress-and-close resume works: reasoning_content + content:\"\".")
    if by.get(3):
        print("  + thinking can be continued mid-trace on the reasoning channel.")
    elif by.get(2):
        print("  - reasoning_content + content:\"\" always CLOSES the block, so the")
        print("    'leave it open' variant needs the inline <think> shape instead.")
    if by.get(4):
        print("  + inline <think> keeps the block open, at the cost of splitting")
        print("    the channels client-side for the rest of that turn.")
    if by.get(6) is False:
        print("  - enable_thinking=false ignored; rethink needs another suppressor.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
