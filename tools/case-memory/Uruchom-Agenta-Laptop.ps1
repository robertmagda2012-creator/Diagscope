# DiagScope - lokalny agent analizujacy zapisany log (bez polaczenia z autem)
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    Add-Type -AssemblyName System.Windows.Forms
    $agent = 'C:\Dev\DiagScope-GitHub\Agent-Prototyp-20261115\bin\Release\net10.0\DiagScope.Agent.exe'
    if (-not (Test-Path -LiteralPath $agent -PathType Leaf)) {
        throw "Nie znaleziono agenta: $agent"
    }

    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = 'DiagScope - wybierz zapisany log do analizy'
    $dlg.Filter = 'Logi diagnostyczne (*.csv;*.jsonl;*.json)|*.csv;*.jsonl;*.json|Wszystkie pliki (*.*)|*.*'
    $dlg.Multiselect = $false
    $dlg.CheckFileExists = $true
    if (Test-Path 'C:\DiagScope\Pojazdy') { $dlg.InitialDirectory = 'C:\DiagScope\Pojazdy' }
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
    $log = $dlg.FileName

    $choice = [System.Windows.Forms.MessageBox]::Show(
        'Czy analizowany pojazd ma instalacje LPG? Wybierz TAK dla LPG, NIE dla benzyny/diesla.',
        'DiagScope - rodzaj pojazdu',
        [System.Windows.Forms.MessageBoxButtons]::YesNoCancel,
        [System.Windows.Forms.MessageBoxIcon]::Question
    )
    if ($choice -eq [System.Windows.Forms.DialogResult]::Cancel) { return }

    $results = 'C:\DiagScope\Analizy-Agenta'
    [IO.Directory]::CreateDirectory($results) | Out-Null
    $name = [IO.Path]::GetFileNameWithoutExtension($log)
    $name = [regex]::Replace($name, '[^a-zA-Z0-9_\-]', '_')
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $prefix = Join-Path $results ($name + '_' + $stamp)
    $json = $prefix + '.json'
    $txt = $prefix + '.txt'

    $arguments = @($log)
    if ($choice -eq [System.Windows.Forms.DialogResult]::Yes) { $arguments += '--lpg' }
    $arguments += @('--output', $json)
    Write-Host ('Analizuje: ' + $log)
    & $agent @arguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $json)) {
        throw ('Agent nie zakonczyl analizy poprawnie (kod ' + $LASTEXITCODE + ').')
    }
    $report = Get-Content -LiteralPath $json -Raw -Encoding UTF8 | ConvertFrom-Json
    $memoryStatus = 'Baza wiedzy: nie zapisano.'
    $memoryTool = 'C:\Dev\DiagScope-Lokalne\DiagScopeCaseMemory.py'
    if (Test-Path -LiteralPath $memoryTool) {
        try {
            $memArgs = @($memoryTool, 'record', $log, $json)
            if ($choice -eq [System.Windows.Forms.DialogResult]::Yes) { $memArgs += '--lpg' }
            $result = & python @memArgs 2>&1
            if ($LASTEXITCODE -eq 0) {
                $memoryStatus = 'Baza wiedzy: zapisano analize (bez potwierdzania naprawy).'
                Write-Host $memoryStatus
            } else {
                $memoryStatus = 'UWAGA: raport utworzony, ale zapis do bazy nie powiodl sie.'
                Write-Warning ($memoryStatus + ' ' + ($result | Out-String))
            }
        }
        catch {
            $memoryStatus = 'UWAGA: zapis do bazy wiedzy nie powiodl sie.'
            Write-Warning ($memoryStatus + ' ' + $_.Exception.Message)
        }
    }
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add('DIAGSCOPE - RAPORT ANALIZY OFFLINE')
    $lines.Add('Log: ' + $log)
    $lines.Add('Data: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    $lines.Add('Kanaly: ' + $report.Channels + '; wiersze: ' + $report.Rows)
    $lines.Add('Zdarzenia: ' + @($report.Findings).Count + '; sugerowane testy: ' + @($report.NextTests).Count)
    $lines.Add('')
    $lines.Add('WYBRANE USTALENIA (maksymalnie 20)')
    foreach ($finding in @($report.Findings) | Select-Object -First 20) {
        $lines.Add(('[' + $finding.Status + '] ' + $finding.Title))
        if ($finding.Reason) { $lines.Add('  ' + $finding.Reason) }
    }
    $lines.Add('')
    $lines.Add('SUGEROWANE TESTY')
    foreach ($test in @($report.NextTests) | Select-Object -First 10) {
        $lines.Add(('[' + $test.Status + '] ' + $test.Title))
        if ($test.Reason) { $lines.Add('  ' + $test.Reason) }
    }
    $lines.Add('')
    $lines.Add('Pełny raport JSON: ' + $json)
    $lines.Add($memoryStatus)
    $lines.Add('Wyniki to analiza zapisanego pliku, nie potwierdzenie usterki samochodu.')
    [IO.File]::WriteAllText($txt, [string]::Join([Environment]::NewLine, $lines), [Text.UTF8Encoding]::new($true))
    Write-Host ('Gotowe. Raport: ' + $txt)
    Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + $txt + '"')
}
catch {
    $message = $_.Exception.Message
    Write-Host ('Blad: ' + $message) -ForegroundColor Red
    [System.Windows.Forms.MessageBox]::Show(
        $message, 'DiagScope - blad uruchomienia',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
}