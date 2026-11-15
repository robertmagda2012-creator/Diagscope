$ErrorActionPreference='Stop'
dotnet build "$PSScriptRoot\DiagScope2.csproj" -c Release --nologo
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project "$PSScriptRoot\Tests" -c Release
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet publish "$PSScriptRoot\Receiver\DiagScope.Receiver.csproj" -c Release --self-contained true -o "$PSScriptRoot\receiver-test" --nologo
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
& "$PSScriptRoot\receiver-test\DiagScope.Receiver.exe" --transport-self-test
exit $LASTEXITCODE