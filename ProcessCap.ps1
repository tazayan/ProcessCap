#requires -Version 5.1

Add-Type -LiteralPath "$PSScriptRoot\ProcessCap.cs"
exit [ProcessCap.Program]::Main()
