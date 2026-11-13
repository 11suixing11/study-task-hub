#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/linux-dev-env.sh"
cd "$taskRoot"
dotnet build src/AccessProbe.LinuxBackend/AccessProbe.LinuxBackend.csproj -c Release --nologo -v quiet
taskGroup="${2:-all}"
case "$taskGroup" in
  dashboard) python3 -m unittest discover -s tests -p test_linux_ui.py -k Dashboard ;;
  editor) python3 -m unittest discover -s tests -p test_linux_ui.py -k Editor ;;
  platforms) python3 -m unittest discover -s tests -p test_linux_ui.py -k Platform ;;
  courses) python3 -m unittest discover -s tests -p test_linux_ui.py -k SmartCourse ;;
  webkit) python3 -m unittest discover -s tests -p test_linux_webkit.py ;;
  all) python3 -m unittest discover -s tests -p 'test_linux_ui.py'; python3 -m unittest discover -s tests -p test_linux_webkit.py ;;
  *) exit 2 ;;
esac
