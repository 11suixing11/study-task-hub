from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src/AccessProbe.Linux"))

def backend_path():
    return ROOT / "src/AccessProbe.LinuxBackend/bin/Release/net10.0/StudyTaskHub.LinuxBackend"
