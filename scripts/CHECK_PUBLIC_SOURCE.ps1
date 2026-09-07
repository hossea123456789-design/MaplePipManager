$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$self = $MyInvocation.MyCommand.Path

$patterns = @(
  'gmail\.com',
  'C:\\Users\\',
  'api[_-]?key',
  'password',
  'secret',
  'Bearer '
)

$extensions = @('*.cs','*.xaml','*.xml','*.md','*.txt','*.json','*.bat','*.ps1','*.csproj')

$files = Get-ChildItem $root -Recurse -File -Include $extensions |
  Where-Object {
    $_.FullName -notmatch '\\artifacts\\' -and
    $_.FullName -ne $self
  }

$found = $false
foreach ($file in $files) {
  foreach ($pattern in $patterns) {
    $matches = Select-String -Path $file.FullName -Pattern $pattern -CaseSensitive:$false
    if ($matches) {
      $found = $true
      $matches | ForEach-Object {
        Write-Host "[CHECK] $($_.Path):$($_.LineNumber) $($_.Line.Trim())"
      }
    }
  }
}

if ($found) {
  Write-Host "Review the matches before publishing." -ForegroundColor Yellow
  exit 2
}

Write-Host "No obvious sensitive-string matches found." -ForegroundColor Green
exit 0
