"""从任意工作目录启动客户端，并让 opuslib 找到随项目提供的 opus.dll。"""

import os
from pathlib import Path
import runpy


def main():
    root = Path(__file__).resolve().parent
    os.chdir(root)
    os.environ["PATH"] = str(root) + os.pathsep + os.environ.get("PATH", "")
    runpy.run_path(str(root / "gui.py"), run_name="__main__")


if __name__ == "__main__":
    main()
