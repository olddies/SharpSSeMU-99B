@echo off
title MuServer Starter
echo ========================================================
echo   Iniciando SSeMU 0.99B (C# Port) - 4 Servidores
echo ========================================================
echo.

set ROOT=%~dp0

echo [1/4] Iniciando ConnectServer (TCP: 44405, UDP: 55557)...
start "MuServer - ConnectServer" cmd /k "cd /d ""%ROOT%src\MuServer.ConnectServer\bin\Debug\net10.0"" && dotnet MuServer.ConnectServer.dll"
timeout /t 2 /nobreak >nul

echo [2/4] Iniciando JoinServer (TCP: 55970)...
start "MuServer - JoinServer" cmd /k "cd /d ""%ROOT%src\MuServer.JoinServer\bin\Debug\net10.0"" && dotnet MuServer.JoinServer.dll"
timeout /t 2 /nobreak >nul

echo [3/4] Iniciando DataServer (TCP: 55960)...
start "MuServer - DataServer" cmd /k "cd /d ""%ROOT%src\MuServer.DataServer\bin\Debug\net10.0"" && dotnet MuServer.DataServer.dll"
timeout /t 2 /nobreak >nul

echo [4/4] Iniciando GameServer (TCP: 55900)...
start "MuServer - GameServer" cmd /k "cd /d ""%ROOT%src\MuServer.GameServer\bin\Debug\net10.0"" && dotnet MuServer.GameServer.dll"

echo.
echo ========================================================
echo   Los 4 servidores se han iniciado en ventanas separadas.
echo ========================================================
pause
