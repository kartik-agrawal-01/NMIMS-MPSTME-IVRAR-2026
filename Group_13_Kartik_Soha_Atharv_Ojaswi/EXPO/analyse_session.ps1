<#
    analyse_session.ps1 - turn one expo session folder into summary.txt + summary.csv

    Reads:  logcat_full.txt      (the [BioEvt] / [BioWin] / Unity lines)
            visitors.csv         (the markers written when you pressed [V])
            pulled\              (score CSVs, templates, gesture files pulled off the headset)

    Writes: summary.txt          human-readable
            summary.csv          one row per metric, for the report
            visitor_timeline.csv one row per visitor with their outcome

    PowerShell only - no Python needed. If Python happens to be installed,
    Tools\analyse_headset_logs.py is run as a bonus, but it is never required.

    Usage:  powershell -ExecutionPolicy Bypass -File analyse_session.ps1 -SessionFolder "<folder>"
            (with no argument it picks the newest folder under EXPO\logs)
#>

param(
    [string]$SessionFolder = ''
)

$ErrorActionPreference = 'Continue'

function Resolve-SessionFolder([string]$given) {
    if ($given -and (Test-Path -LiteralPath $given)) { return (Resolve-Path -LiteralPath $given).Path }
    $logs = Join-Path $PSScriptRoot 'logs'
    if (-not (Test-Path -LiteralPath $logs)) { return $null }
    $newest = Get-ChildItem -LiteralPath $logs -Directory -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { return $newest.FullName }
    return $null
}

$session = Resolve-SessionFolder $SessionFolder
if (-not $session) {
    Write-Host "No session folder found. Pass -SessionFolder <path>." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Analysing session: $session"

$logcatPath  = Join-Path $session 'logcat_full.txt'
$visitorPath = Join-Path $session 'visitors.csv'
$pulledPath  = Join-Path $session 'pulled'

$lines = @()
if (Test-Path -LiteralPath $logcatPath) {
    $lines = Get-Content -LiteralPath $logcatPath -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- visitors
$visitors = @()
if (Test-Path -LiteralPath $visitorPath) {
    $visitors = @(Import-Csv -LiteralPath $visitorPath -ErrorAction SilentlyContinue |
                  Where-Object { $_.visitor })
}

function Get-LineClock([string]$line) {
    # logcat -v time lines start like: 01-01 10:02:06.100
    $m = [regex]::Match($line, '^(\d\d-\d\d\s+\d\d:\d\d:\d\d\.\d+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Get-LineSeconds([string]$line) {
    $m = [regex]::Match($line, '^\d\d-\d\d\s+(\d\d):(\d\d):(\d\d)\.(\d+)')
    if (-not $m.Success) { return $null }
    return ([double]$m.Groups[1].Value) * 3600 + ([double]$m.Groups[2].Value) * 60 +
           ([double]$m.Groups[3].Value) + ([double]("0." + $m.Groups[4].Value))
}

function Owner-Of([int]$index) {
    $owner = $null
    foreach ($mk in $markers) { if ($mk.Index -le $index) { $owner = $mk } else { break } }
    return $owner
}

# Visitor markers inside logcat carry the line index, which lets us attribute
# every later event to the visitor who was wearing the headset at the time.
$markers = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $lines.Count; $i++) {
    $m = [regex]::Match($lines[$i], '\[Visitor\]\s+(\S+)\s+scenario=(\S+)\s+badge=(\S+)')
    if ($m.Success) {
        [void]$markers.Add([pscustomobject]@{
            Index    = $i
            Visitor  = $m.Groups[1].Value
            Scenario = $m.Groups[2].Value
            Badge    = $m.Groups[3].Value
            Clock    = (Get-LineClock $lines[$i])
        })
    }
}

# ------------------------------------------------------------ event parsing
$events = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $lines.Count; $i++) {
    $m = [regex]::Match($lines[$i], '\[BioEvt\]\s+t=([-\d\.]+)\s+type=(\S+)\s+user=(\S+)\s+detail=(.*)$')
    if (-not $m.Success) { continue }
    [void]$events.Add([pscustomobject]@{
        Index   = $i
        T       = [double]$m.Groups[1].Value
        Type    = $m.Groups[2].Value
        User    = $m.Groups[3].Value
        Detail  = $m.Groups[4].Value.Trim()
        Seconds = (Get-LineSeconds $lines[$i])
        Owner   = $null
    })
}
foreach ($e in $events) { $e.Owner = (Owner-Of $e.Index) }

$windows = New-Object System.Collections.ArrayList
foreach ($line in $lines) {
    $m = [regex]::Match($line, '\[BioWin\]\s+user=(\S+)\s+t=([-\d\.]+)\s+state=(\S+)\s+raw=([-\d\.]+)\s+smoothed=([-\d\.]+)\s+viol=(\d+)(?:\s+preset=(\S+))?')
    if (-not $m.Success) { continue }
    [void]$windows.Add([pscustomobject]@{
        User     = $m.Groups[1].Value
        T        = [double]$m.Groups[2].Value
        State    = $m.Groups[3].Value
        Raw      = [double]$m.Groups[4].Value
        Smoothed = [double]$m.Groups[5].Value
        Viol     = [int]$m.Groups[6].Value
        Preset   = $(if ($m.Groups[7].Success) { $m.Groups[7].Value } else { '' })
    })
}

function Count-Type([string]$t) { return @($events | Where-Object { $_.Type -eq $t }).Count }

$gestureScores = @()
foreach ($e in $events) {
    if ($e.Type -eq 'gesture_pass' -or $e.Type -eq 'gesture_fail') {
        $m = [regex]::Match($e.Detail, 'score=([\d\.]+)')
        if ($m.Success) {
            $gestureScores += [pscustomobject]@{
                Score   = [double]$m.Groups[1].Value
                Passed  = ($e.Type -eq 'gesture_pass')
                Visitor = $(if ($e.Owner) { $e.Owner.Visitor } else { '' })
            }
        }
    }
}

# --------------------------------------------- lockouts and their latencies
$lockouts = New-Object System.Collections.ArrayList
foreach ($e in $events) {
    if ($e.Type -ne 'lockout' -and $e.Type -ne 'forced_lockout') { continue }

    $latency = ''
    if ($e.Owner -and $e.Seconds -ne $null) {
        $markerSeconds = Get-LineSeconds $lines[$e.Owner.Index]
        if ($markerSeconds -ne $null) {
            $d = $e.Seconds - $markerSeconds
            if ($d -ge 0) { $latency = [math]::Round($d, 2) }
        }
    }
    $top = ''
    $mt = [regex]::Match($e.Detail, 'top=(\S+)')
    if ($mt.Success) { $top = $mt.Groups[1].Value }

    [void]$lockouts.Add([pscustomobject]@{
        Visitor  = $(if ($e.Owner) { $e.Owner.Visitor } else { '' })
        Scenario = $(if ($e.Owner) { $e.Owner.Scenario } else { '' })
        Badge    = $(if ($e.Owner) { $e.Owner.Badge } else { $e.User })
        User     = $e.User
        Forced   = ($e.Type -eq 'forced_lockout')
        Latency  = $latency
        TopFeature = $top
        Detail   = $e.Detail
    })
}

# S1 is the genuine scenario: a lockout there is a FALSE lockout.
# S2, S3, S4 are impostor scenarios: a lockout there is a DETECTION.
$genuineScenarios  = @('S1', 'S5')
$impostorScenarios = @('S2', 'S3', 'S4')

$realLockouts   = @($lockouts | Where-Object { -not $_.Forced })
$falseLockouts  = @($realLockouts | Where-Object { $genuineScenarios -contains $_.Scenario })
$detections     = @($realLockouts | Where-Object { $impostorScenarios -contains $_.Scenario })
$forcedLockouts = @($lockouts | Where-Object { $_.Forced })

$genuineVisits  = @($visitors | Where-Object { $genuineScenarios -contains $_.scenario })
$impostorVisits = @($visitors | Where-Object { $impostorScenarios -contains $_.scenario })

$detectionLatencies = @($detections | Where-Object { $_.Latency -ne '' } | ForEach-Object { [double]$_.Latency })

# ------------------------------------------------------------------ presets
$presetsUsed = @($windows | Where-Object { $_.Preset } | Select-Object -ExpandProperty Preset -Unique)
$presetChanges = @($events | Where-Object { $_.Type -eq 'preset_change' })

# ------------------------------------------------------- pulled file counts
function Count-Files([string]$sub, [string]$pattern) {
    $p = Join-Path $pulledPath $sub
    if (-not (Test-Path -LiteralPath $p)) { return 0 }
    return @(Get-ChildItem -LiteralPath $p -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue).Count
}

$scoreCsvCount   = Count-Files 'BiometricLogs' 'scores_*.csv'
$sessionLogCount = Count-Files 'MFALogs' 'session_*.log'
$templateCount   = Count-Files 'BiometricTemplates' 'template_*.json'
$mimicryCount    = Count-Files 'BiometricTemplates' 'mimicry_*.json'
$gestureCount    = Count-Files 'MFAEnrolment' 'gesture_*.json'

# ----------------------------------------------------------------- MFA tally
$mfaPasses      = Count-Type 'mfa_pass'
$gesturePasses  = Count-Type 'gesture_pass'
$gestureFails   = Count-Type 'gesture_fail'
$gestureEnrols  = Count-Type 'gesture_enrol'
$gestureRestart = Count-Type 'gesture_enrol_restart'
$enrolStarts    = Count-Type 'enrol_start'
$enrolDone      = Count-Type 'enrol_done'
$identitySwitch = Count-Type 'identity_switch'
$adaptSaved     = Count-Type 'adaptation_saved'
$mimicryStarts  = Count-Type 'mimicry_start'

# PIN and OTP outcomes are only in the session log text, not in [BioEvt].
$pinRejected = @($lines | Where-Object { $_ -match 'PIN check for .*rejected' }).Count
$pinAccepted = @($lines | Where-Object { $_ -match 'PIN check for .*accepted' }).Count
$otpRejected = @($lines | Where-Object { $_ -match 'OTP check for .*rejected' }).Count
$otpAccepted = @($lines | Where-Object { $_ -match 'OTP check for .*accepted' }).Count
$timeouts    = @($lines | Where-Object { $_ -match 'TIMED OUT' }).Count

$scoreStats = $null
if ($windows.Count -gt 0) {
    $verified = @($windows | Where-Object { $_.State -eq 'Verified' } | Select-Object -ExpandProperty Smoothed)
    $scoreStats = [pscustomobject]@{
        Windows      = $windows.Count
        VerifiedMean = $(if ($verified.Count) { [math]::Round(($verified | Measure-Object -Average).Average, 3) } else { '' })
        WarningCount = @($windows | Where-Object { $_.State -eq 'Warning' }).Count
        LockedCount  = @($windows | Where-Object { $_.State -eq 'LockedOut' }).Count
    }
}

# ================================================================== output
$txt = New-Object System.Collections.ArrayList
function W([string]$s) { [void]$txt.Add($s) }

W "BoardroomXR - expo session summary"
W "=================================="
W "session folder : $session"
W "generated      : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W "logcat lines   : $($lines.Count)"
W ""
W "VISITORS"
W "  markers in visitors.csv : $($visitors.Count)"
W "  markers found in logcat : $($markers.Count)"
if ($visitors.Count) {
    $byScenario = $visitors | Group-Object scenario | Sort-Object Name
    foreach ($g in $byScenario) { W "    $($g.Name) : $($g.Count) visit(s)" }
    $badges = @($visitors | Select-Object -ExpandProperty badge -Unique)
    W "  badges used : $($badges -join ', ')"
}
W ""
W "MFA FACTORS"
W "  PIN      accepted $pinAccepted / rejected $pinRejected"
W "  gesture  passed   $gesturePasses / failed  $gestureFails   (enrolments $gestureEnrols, restarts $gestureRestart)"
W "  OTP      accepted $otpAccepted / rejected $otpRejected"
W "  full MFA passes   $mfaPasses"
W "  session timeouts  $timeouts"
if ($gestureScores.Count) {
    $gs = $gestureScores | Select-Object -ExpandProperty Score
    $stats = $gs | Measure-Object -Average -Minimum -Maximum
    W ("  gesture scores    n=$($gs.Count) mean={0:N3} min={1:N3} max={2:N3}" -f $stats.Average, $stats.Minimum, $stats.Maximum)
}
W ""
W "BEHAVIOURAL ENROLMENT"
W "  enrolments started   : $enrolStarts"
W "  enrolments completed : $enrolDone"
W "  identity switches    : $identitySwitch"
W "  adapted templates saved : $adaptSaved"
W ""
W "CONTINUOUS VERIFICATION"
if ($scoreStats) {
    W "  scored windows : $($scoreStats.Windows)  (Warning $($scoreStats.WarningCount), LockedOut $($scoreStats.LockedCount))"
    W "  mean smoothed score while Verified : $($scoreStats.VerifiedMean)"
} else {
    W "  no [BioWin] lines found"
}
if ($presetsUsed.Count) { W "  preset(s) seen in windows : $($presetsUsed -join ', ')" }
W "  preset changes during the session : $($presetChanges.Count)"
foreach ($pc in $presetChanges) { W "    -> $($pc.Detail)" }
W ""
W "LOCKOUTS"
W "  real lockouts  : $($realLockouts.Count)"
W "  forced (demo)  : $($forcedLockouts.Count)"
W "  false lockouts on genuine scenarios (S1/S5) : $($falseLockouts.Count) of $($genuineVisits.Count) genuine visit(s)"
W "  detections on impostor scenarios (S2/S3/S4) : $($detections.Count) of $($impostorVisits.Count) impostor visit(s)"
if ($detectionLatencies.Count) {
    $ls = $detectionLatencies | Measure-Object -Average -Minimum -Maximum
    W ("  detection latency after the visitor marker : mean {0:N2} s, min {1:N2} s, max {2:N2} s" -f $ls.Average, $ls.Minimum, $ls.Maximum)
    W "  (measured from the [V] marker, so it includes the time the examiner spent logging in)"
}
foreach ($l in $lockouts) {
    $kind = $(if ($l.Forced) { 'FORCED' } else { 'real  ' })
    W "    $kind $($l.Visitor) $($l.Scenario) $($l.Badge) latency=$($l.Latency) top=$($l.TopFeature)"
}
W ""
W "MIMICRY ATTACKS"
W "  attempts started : $mimicryStarts"
foreach ($e in @($events | Where-Object { $_.Type -eq 'mimicry_stop' })) { W "    outcome: $($e.Detail)" }
W ""
W "EVIDENCE PULLED OFF THE HEADSET"
W "  score CSVs            : $scoreCsvCount"
W "  session logs          : $sessionLogCount"
W "  behavioural templates : $templateCount"
W "  mimicry recordings    : $mimicryCount"
W "  gesture enrolments    : $gestureCount"
if (($scoreCsvCount + $templateCount) -eq 0) {
    W "  WARNING: nothing was pulled. Was the headset connected when you pressed [P] or [Q]?"
}
W ""
W "EVENT TYPE TALLY"
foreach ($g in @($events | Group-Object Type | Sort-Object Count -Descending)) {
    W ("  {0,-24} {1}" -f $g.Name, $g.Count)
}

$summaryTxt = Join-Path $session 'summary.txt'
$txt | Set-Content -LiteralPath $summaryTxt -Encoding UTF8
Write-Host "  wrote $summaryTxt"

# ------------------------------------------------------------- summary.csv
$rows = New-Object System.Collections.ArrayList
function Row([string]$metric, $value, [string]$note) {
    [void]$rows.Add([pscustomobject]@{ metric = $metric; value = $value; note = $note })
}
Row 'session_folder'            $session ''
Row 'logcat_lines'              $lines.Count ''
Row 'visitors_marked'           $visitors.Count 'from visitors.csv'
Row 'visitor_markers_in_logcat' $markers.Count ''
Row 'pin_accepted'              $pinAccepted ''
Row 'pin_rejected'              $pinRejected ''
Row 'gesture_passed'            $gesturePasses ''
Row 'gesture_failed'            $gestureFails ''
Row 'gesture_enrolments'        $gestureEnrols '3-stroke enrolments completed'
Row 'gesture_enrol_restarts'    $gestureRestart 'inconsistent stroke, enrolment restarted'
Row 'otp_accepted'              $otpAccepted ''
Row 'otp_rejected'              $otpRejected ''
Row 'mfa_passes'                $mfaPasses 'all three factors'
Row 'session_timeouts'          $timeouts ''
Row 'enrolments_started'        $enrolStarts ''
Row 'enrolments_completed'      $enrolDone ''
Row 'identity_switches'         $identitySwitch 'badges presented'
Row 'adapted_templates_saved'   $adaptSaved ''
Row 'scored_windows'            $(if ($scoreStats) { $scoreStats.Windows } else { 0 }) ''
Row 'mean_score_while_verified' $(if ($scoreStats) { $scoreStats.VerifiedMean } else { '' }) 'smoothed'
Row 'warning_windows'           $(if ($scoreStats) { $scoreStats.WarningCount } else { 0 }) ''
Row 'lockedout_windows'         $(if ($scoreStats) { $scoreStats.LockedCount } else { 0 }) ''
Row 'presets_seen'              ($presetsUsed -join '|') ''
Row 'preset_changes'            $presetChanges.Count ''
Row 'lockouts_real'             $realLockouts.Count ''
Row 'lockouts_forced_demo'      $forcedLockouts.Count 'presenter console, not a detection'
Row 'genuine_visits_S1_S5'      $genuineVisits.Count ''
Row 'false_lockouts'            $falseLockouts.Count 'lockout during a genuine scenario'
Row 'impostor_visits_S2_S3_S4'  $impostorVisits.Count ''
Row 'detections'                $detections.Count 'lockout during an impostor scenario'
if ($detectionLatencies.Count) {
    $ls = $detectionLatencies | Measure-Object -Average -Minimum -Maximum
    Row 'detection_latency_mean_s' ([math]::Round($ls.Average, 2)) 'from the visitor marker'
    Row 'detection_latency_min_s'  ([math]::Round($ls.Minimum, 2)) ''
    Row 'detection_latency_max_s'  ([math]::Round($ls.Maximum, 2)) ''
}
if ($gestureScores.Count) {
    $gs = $gestureScores | Select-Object -ExpandProperty Score
    $stats = $gs | Measure-Object -Average -Minimum -Maximum
    Row 'gesture_score_mean' ([math]::Round($stats.Average, 3)) ''
    Row 'gesture_score_min'  ([math]::Round($stats.Minimum, 3)) ''
    Row 'gesture_score_max'  ([math]::Round($stats.Maximum, 3)) ''
}
Row 'mimicry_attempts'      $mimicryStarts ''
Row 'pulled_score_csvs'     $scoreCsvCount ''
Row 'pulled_session_logs'   $sessionLogCount ''
Row 'pulled_templates'      $templateCount ''
Row 'pulled_mimicry'        $mimicryCount ''
Row 'pulled_gesture_files'  $gestureCount ''

$summaryCsv = Join-Path $session 'summary.csv'
$rows | Export-Csv -LiteralPath $summaryCsv -NoTypeInformation -Encoding UTF8
Write-Host "  wrote $summaryCsv"

# ------------------------------------------------------ visitor_timeline.csv
$timeline = New-Object System.Collections.ArrayList
foreach ($v in $visitors) {
    $mk = @($markers | Where-Object { $_.Visitor -eq $v.visitor }) | Select-Object -First 1
    $lk = @($lockouts | Where-Object { $_.Visitor -eq $v.visitor }) | Select-Object -First 1
    $ev = @()
    if ($mk) { $ev = @($events | Where-Object { $_.Owner -and $_.Owner.Visitor -eq $v.visitor }) }
    [void]$timeline.Add([pscustomobject]@{
        visitor   = $v.visitor
        scenario  = $v.scenario
        badge     = $v.badge
        marked_at = $v.timestamp
        mfa_pass  = @($ev | Where-Object { $_.Type -eq 'mfa_pass' }).Count
        enrolled  = @($ev | Where-Object { $_.Type -eq 'enrol_done' }).Count
        lockout   = $(if ($lk) { if ($lk.Forced) { 'forced' } else { 'yes' } } else { 'no' })
        latency_s = $(if ($lk) { $lk.Latency } else { '' })
        top_feature = $(if ($lk) { $lk.TopFeature } else { '' })
        note      = $v.note
    })
}
$timelineCsv = Join-Path $session 'visitor_timeline.csv'
if ($timeline.Count) {
    $timeline | Export-Csv -LiteralPath $timelineCsv -NoTypeInformation -Encoding UTF8
    Write-Host "  wrote $timelineCsv"
}

# ------------------------------------------- optional Python extra analysis
$py = Get-Command python -ErrorAction SilentlyContinue
$pyScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'Tools\analyse_headset_logs.py'
if ($py -and (Test-Path -LiteralPath $pyScript)) {
    Write-Host "  Python found - running the optional extra analysis (failures are ignored)..."
    try {
        # Out-File with utf8 rather than Tee-Object: Tee writes UTF-16 on
        # Windows PowerShell 5.1, which makes the file look like mojibake.
        & $py.Source $pyScript $session 2>&1 |
            Out-File -LiteralPath (Join-Path $session 'python_analysis.txt') -Encoding utf8
        Write-Host "  wrote $(Join-Path $session 'python_analysis.txt')"
    } catch {
        Write-Host "  optional Python analysis failed (ignored): $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  (Python not installed or Tools\analyse_headset_logs.py missing - skipped, not required)"
}

Write-Host ""
Write-Host "---- summary.txt ----"
Get-Content -LiteralPath $summaryTxt | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
Write-Host "---------------------"
Write-Host ""
exit 0
