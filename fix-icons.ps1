$path = Join-Path $PSScriptRoot "HealthTabPage.xaml"
$bytes = [System.IO.File]::ReadAllBytes($path)

# Find the second occurrence of "Text=" that follows "StopButton" context
# The Stop button TextBlock has corrupted icon bytes
# Search for the pattern: after "StopButton" context, find TextBlock with Text="<corrupted>"
# We know the Stop button area starts around offset 2442

# Look for the specific byte sequence around the stop icon
# The corrupted bytes for the stop icon (U+23F9 ⏹ double-encoded) are: C3 A3 E2 8F B9
$searchStop = [byte[]](0xC3, 0xA3, 0xE2, 0x8F, 0xB9)
$replacement = [System.Text.Encoding]::UTF8.GetBytes("&#xE71A;")

Write-Host "File size: $($bytes.Length) bytes"
Write-Host "Searching for stop icon mojibake: C3 A3 E2 8F B9"

$found = $false
for ($i = 0; $i -lt $bytes.Length - $searchStop.Length; $i++) {
    $match = $true
    for ($j = 0; $j -lt $searchStop.Length; $j++) {
        if ($bytes[$i + $j] -ne $searchStop[$j]) {
            $match = $false
            break
        }
    }
    if ($match) {
        Write-Host "Found mojibake at offset $i"
        $found = $true
        
        # Replace the corrupted bytes
        $newBytes = [byte[]]::new($bytes.Length - $searchStop.Length + $replacement.Length)
        [Array]::Copy($bytes, 0, $newBytes, 0, $i)
        [Array]::Copy($replacement, 0, $newBytes, $i, $replacement.Length)
        [Array]::Copy($bytes, $i + $searchStop.Length, $newBytes, $i + $replacement.Length, $bytes.Length - $i - $searchStop.Length)
        
        # Also need to add FontFamily="Segoe MDL2 Assets" after the TextBlock Text attribute
        # Find the FontSize= that follows
        $fontSizeBytes = [System.Text.Encoding]::UTF8.GetBytes(' FontSize="14"')
        $fontFamilyInsert = [System.Text.Encoding]::UTF8.GetBytes(' FontFamily="Segoe MDL2 Assets"')
        
        # Search for FontSize= after the replacement point
        $searchFrom = $i + $replacement.Length
        $insertPos = -1
        for ($k = $searchFrom; $k -lt [Math]::Min($searchFrom + 30, $newBytes.Length - $fontSizeBytes.Length); $k++) {
            $matchFs = $true
            for ($l = 0; $l -lt $fontSizeBytes.Length; $l++) {
                if ($newBytes[$k + $l] -ne $fontSizeBytes[$l]) {
                    $matchFs = $false
                    break
                }
            }
            if ($matchFs) {
                $insertPos = $k
                break
            }
        }
        
        if ($insertPos -ge 0) {
            $finalBytes = [byte[]]::new($newBytes.Length + $fontFamilyInsert.Length)
            [Array]::Copy($newBytes, 0, $finalBytes, 0, $insertPos)
            [Array]::Copy($fontFamilyInsert, 0, $finalBytes, $insertPos, $fontFamilyInsert.Length)
            [Array]::Copy($newBytes, $insertPos, $finalBytes, $insertPos + $fontFamilyInsert.Length, $newBytes.Length - $insertPos)
            $newBytes = $finalBytes
        }
        
        [System.IO.File]::WriteAllBytes($path, $newBytes)
        Write-Host "FIXED: Replaced corrupted Stop icon with E71A + FontFamily"
        break
    }
}

if (-not $found) {
    Write-Host "Mojibake pattern not found. Trying alternate patterns..."
    
    # Try: C3 A3 E2 80 (common mojibake start)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 0xC3 -and $bytes[$i+1] -eq 0xA3 -and $bytes[$i+2] -eq 0xE2) {
            $hex = ""
            for ($j = $i; $j -lt [Math]::Min($i + 10, $bytes.Length); $j++) {
                $hex += ('{0:X2} ' -f $bytes[$j])
            }
            Write-Host "Potential mojibake at offset $i : $hex"
        }
    }
}
