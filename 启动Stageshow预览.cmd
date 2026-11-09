@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-stageshow.ps1"
echo.
echo 打开浏览器，访问 http://127.0.0.1:4280/?workbench
pause
