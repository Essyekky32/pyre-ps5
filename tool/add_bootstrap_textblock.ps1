# Insert the BootstrapText line into the Host card, after PayloadText.
$f = 'D:\Projects\PYRE\tool\Pyre\MainWindow.xaml'
$c = Get-Content $f -Raw

if ($c -match 'x:Name="BootstrapText"') {
    Write-Output "already present"
    exit 0
}

$pattern = '(\s*<TextBlock x:Name="PayloadText"[^>]*?/>)'
if ($c -notmatch $pattern) {
    Write-Output "FAILED: could not find the PayloadText element"
    exit 1
}

$insert = @'
$1

                <TextBlock x:Name="BootstrapText" Style="{StaticResource Small}"
                           Foreground="{StaticResource Muted}" Margin="0,6,0,0" />
'@

$new = [regex]::Replace($c, $pattern, $insert, 1)
Set-Content -Path $f -Value $new -NoNewline -Encoding UTF8

Write-Output "inserted BootstrapText"
Select-String -Path $f -Pattern 'BootstrapText' | ForEach-Object { "  $($_.LineNumber): $($_.Line.Trim())" }
