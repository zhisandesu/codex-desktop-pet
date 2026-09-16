"""Persistent, local-only Whisper worker for XiaobianPet.

Protocol: UTF-8 JSON lines on stdin/stdout. Diagnostic output always goes to
stderr so the C# side can treat stdout as a clean machine protocol.
"""

from __future__ import annotations

import argparse
import gc
import importlib.util
import json
import os
import subprocess
import sys
import traceback
from pathlib import Path


DLL_HANDLES: list[object] = []
SENSITIVE_ENVIRONMENT_MARKERS = (
    "API_KEY",
    "APIKEY",
    "ACCESS_KEY",
    "CONNECTION_STRING",
)


def environment_name_looks_sensitive(name: str) -> bool:
    normalized = name.upper()
    return (
        any(marker in normalized for marker in SENSITIVE_ENVIRONMENT_MARKERS)
        or normalized == "TOKEN"
        or normalized.endswith("_TOKEN")
        or "_TOKEN_" in normalized
        or normalized == "SECRET"
        or normalized.endswith("_SECRET")
        or "_SECRET_" in normalized
        or normalized == "PASSWORD"
        or normalized.endswith("_PASSWORD")
        or normalized == "CREDENTIAL"
        or normalized.endswith("_CREDENTIAL")
    )


def emit(payload: dict[str, object]) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def log(message: str) -> None:
    sys.stderr.write(message.rstrip() + "\n")
    sys.stderr.flush()


def add_cuda_runtime_paths() -> None:
    """Let CTranslate2 reuse the CUDA/cuDNN DLLs bundled with local PyTorch."""
    candidates: list[Path] = []
    torch_spec = importlib.util.find_spec("torch")
    if torch_spec is not None and torch_spec.origin:
        candidates.append(Path(torch_spec.origin).resolve().parent / "lib")

    configured = os.environ.get("XIAOBIAN_CUDA_DLL_DIR", "").strip()
    if configured:
        candidates.insert(0, Path(configured))

    for candidate in candidates:
        if not candidate.is_dir():
            continue
        os.environ["PATH"] = str(candidate) + os.pathsep + os.environ.get("PATH", "")
        if hasattr(os, "add_dll_directory"):
            try:
                DLL_HANDLES.append(os.add_dll_directory(str(candidate)))
            except OSError:
                pass


def free_cuda_memory_megabytes() -> int | None:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=memory.free",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            timeout=4,
            check=True,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        first_line = completed.stdout.strip().splitlines()[0]
        return int(first_line.strip())
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return None


def preferred_backends(free_memory: int | None) -> list[tuple[str, str]]:
    backends: list[tuple[str, str]] = []
    if free_memory is None or free_memory >= 5200:
        backends.append(("cuda", "float16"))
    backends.extend((("cuda", "int8_float16"), ("cuda", "int8"), ("cpu", "int8")))
    return backends


def warm_up_model(model) -> None:
    """Force one tiny decode so deferred CUDA failures happen before ready."""
    import numpy as np

    segments, _ = model.transcribe(
        np.zeros(3200, dtype=np.float32),
        language="zh",
        task="transcribe",
        beam_size=1,
        temperature=0.0,
        condition_on_previous_text=False,
        vad_filter=False,
        without_timestamps=True,
    )
    list(segments)


def load_model(
    model_path: str,
    backends: list[tuple[str, str]],
    free_memory: int | None,
):
    add_cuda_runtime_paths()
    from faster_whisper import WhisperModel

    failures: list[str] = []
    for device, compute_type in backends:
        try:
            log(
                f"Loading Whisper model with device={device}, compute_type={compute_type}, "
                f"free_cuda_mb={free_memory}."
            )
            model = WhisperModel(
                model_path,
                device=device,
                compute_type=compute_type,
                cpu_threads=max(4, min(12, os.cpu_count() or 4)),
                num_workers=1,
                local_files_only=True,
            )
            warm_up_model(model)
            return model, device, compute_type, free_memory
        except Exception as exception:  # noqa: BLE001 - each backend is an intentional fallback
            failures.append(f"{device}/{compute_type}: {type(exception).__name__}: {exception}")
            log(f"Whisper backend failed: {failures[-1]}")

    raise RuntimeError("; ".join(failures) or "No local Whisper backend is available.")


def release_backend() -> None:
    gc.collect()
    try:
        import torch

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:  # noqa: BLE001 - cleanup must not hide the real decode error
        pass


def transcribe(model, audio_path: str) -> tuple[str, float | None]:
    segments, info = model.transcribe(
        audio_path,
        language="zh",
        task="transcribe",
        beam_size=5,
        temperature=0.0,
        condition_on_previous_text=False,
        vad_filter=True,
        vad_parameters={
            "min_silence_duration_ms": 420,
            "speech_pad_ms": 140,
        },
        hotwords="小编龙 主人 Codex Luna 方舟 豆包 皮公主 调皮公主",
        word_timestamps=False,
        without_timestamps=True,
    )
    text = "".join(segment.text for segment in segments).strip()
    probability = getattr(info, "language_probability", None)
    return text, float(probability) if probability is not None else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    args = parser.parse_args()

    model_directory = Path(args.model).expanduser().resolve()
    required_files = ("model.bin", "config.json", "tokenizer.json")
    if any(not (model_directory / file_name).is_file() for file_name in required_files):
        emit(
            {
                "type": "fatal",
                "error": f"本地 Whisper 模型不完整：{model_directory}",
            }
        )
        return 2

    free_memory = free_cuda_memory_megabytes()
    backends = preferred_backends(free_memory)
    try:
        model, device, compute_type, free_memory = load_model(
            str(model_directory), backends, free_memory
        )
    except Exception as exception:  # noqa: BLE001 - surface a compact startup error to the UI
        log(traceback.format_exc())
        emit({"type": "fatal", "error": f"Whisper 模型启动失败：{exception}"})
        return 3

    emit(
        {
            "type": "ready",
            "protocolVersion": 1,
            "sensitiveEnvironmentSanitized": not any(
                value and environment_name_looks_sensitive(name)
                for name, value in os.environ.items()
            ),
            "device": device,
            "computeType": compute_type,
            "freeCudaMbAtLoad": free_memory,
        }
    )

    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue

        request_id = ""
        try:
            request = json.loads(raw_line)
            request_id = str(request.get("id", ""))
            audio_path = str(request.get("audioPath", ""))
            if not request_id or not audio_path:
                raise ValueError("转写请求缺少 id 或 audioPath。")
            if not Path(audio_path).is_file():
                raise FileNotFoundError(f"录音文件不存在：{audio_path}")

            try:
                text, language_probability = transcribe(model, audio_path)
            except Exception as first_exception:  # noqa: BLE001 - retry on a safer backend
                if device != "cuda":
                    raise

                log(
                    "Whisper CUDA decode failed; retiring this backend and retrying: "
                    f"{type(first_exception).__name__}: {first_exception}"
                )
                current_index = backends.index((device, compute_type))
                remaining_backends = backends[current_index + 1 :]
                if not remaining_backends:
                    raise

                model = None
                release_backend()
                model, device, compute_type, free_memory = load_model(
                    str(model_directory), remaining_backends, free_memory
                )
                emit(
                    {
                        "type": "backend",
                        "device": device,
                        "computeType": compute_type,
                        "freeCudaMbAtLoad": free_memory,
                    }
                )
                text, language_probability = transcribe(model, audio_path)
            emit(
                {
                    "type": "result",
                    "id": request_id,
                    "text": text,
                    "languageProbability": language_probability,
                }
            )
        except Exception as exception:  # noqa: BLE001 - one bad turn must not kill the warm model
            log(traceback.format_exc())
            emit(
                {
                    "type": "error",
                    "id": request_id,
                    "error": f"本地语音识别失败：{type(exception).__name__}: {exception}",
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
