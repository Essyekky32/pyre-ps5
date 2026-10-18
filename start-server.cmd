@echo off
setlocal enableextensions
cd /d "%~dp0"

rem --- find a real Python. The Windows Store 'python.exe' stub in System32
rem --- does not run scripts, so it is explicitly skipped.
set "PY="
for /d %%D in ("%LOCALAPPDATA%\Programs\Python\Python*") do (
  if exist "%%~fD\python.exe" set "PY=%%~fD\python.exe"
)
if not defined PY (
  for /d %%D in ("C:\Program Files\Python*") do (
    if exist "%%~fD\python.exe" set "PY=%%~fD\python.exe"
  )
)
if not defined PY (
  for /f "delims=" %%P in ('where py 2^>nul') do if not defined PY set "PY=%%P -3"
)

if not defined PY (
  echo.
  echo   No real Python found on this PC.
  echo   Install Python 3 from python.org, then run this again.
  echo.
  pause
  exit /b 1
)

echo.
echo   PYRE self-host
echo   --------------
echo   Using:    %PY%
echo   Serving:  %~dp0 on port 8000
echo   On the PS5, open the http://192.168.x.x:8000/ address printed below.
echo   Press Ctrl+C to stop.
echo.
%PY% serve.py

echo.
echo   Server stopped.
pause
