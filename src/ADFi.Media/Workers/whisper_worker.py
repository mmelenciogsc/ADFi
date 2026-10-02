"""Bounded NDJSON worker for the official openai-whisper Python package."""

import argparse
import contextlib
import gc
import json
import math
import sys


def emit(value):
    # The worker protocol must remain independent of the Windows console code page. JSON Unicode
    # escapes round-trip to the original text in .NET and prevent cp1252/charmap failures when a
    # transcript contains characters not representable by the active legacy console encoding.
    sys.stdout.write(json.dumps(value, ensure_ascii=True, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", default="cpu", choices=("cpu", "cuda"))
    parser.add_argument("--download-root", required=True)
    args = parser.parse_args()
    model = None

    for raw_line in sys.stdin:
        try:
            command = json.loads(raw_line)
            command_type = command.get("type")
            if command_type == "load":
                import whisper
                with contextlib.redirect_stdout(sys.stderr):
                    model = whisper.load_model(
                        command["model"],
                        device=args.device,
                        download_root=args.download_root,
                    )
                emit({"ok": True})
            elif command_type == "transcribe":
                if model is None:
                    raise RuntimeError("Whisper model is not loaded")
                with contextlib.redirect_stdout(sys.stderr):
                    result = model.transcribe(
                        command["path"],
                        fp16=args.device == "cuda",
                        verbose=False,
                        word_timestamps=True,
                        condition_on_previous_text=False,
                    )
                segments = []
                for segment in result.get("segments", []):
                    confidence = min(1.0, max(0.0, math.exp(segment.get("avg_logprob", -20.0))))
                    words = []
                    for word in segment.get("words", []):
                        text = word.get("word", "").strip()
                        start = float(word.get("start", segment["start"]))
                        end = float(word.get("end", segment["end"]))
                        if not text or end <= start:
                            continue
                        words.append({
                            "text": text,
                            "startSeconds": start,
                            "endSeconds": end,
                            "confidence": word.get("probability"),
                        })
                    segments.append({
                        "text": segment.get("text", "").strip(),
                        "startSeconds": float(segment["start"]),
                        "endSeconds": float(segment["end"]),
                        "confidence": confidence,
                        "words": words,
                    })
                emit({"ok": True, "segments": segments})
            elif command_type == "unload":
                model = None
                gc.collect()
                try:
                    import torch
                    if torch.cuda.is_available():
                        torch.cuda.empty_cache()
                except ImportError:
                    pass
                emit({"ok": True})
            elif command_type == "exit":
                emit({"ok": True})
                return
            else:
                raise ValueError("Unsupported worker command")
        except Exception as error:
            emit({"ok": False, "error": f"{type(error).__name__}: {error}"})


if __name__ == "__main__":
    main()
