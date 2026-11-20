@echo off
rem One-shot execution-policy bypass for this download script only.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-nestest.ps1"
