# RobLock history merge generator for Windows PowerShell 5.1 or newer.
# Run from the website folder: .\merge_roblock_history.ps1 -Source .\data\db.json
# Does not connect to Supabase, write to db.json, or change hardware settings.
param(
    [string]$Source = '.\data\db.json',
    [string]$Output = '.\roblock_merge_fixed.sql'
)
$ErrorActionPreference = 'Stop'
function Sql-Literal($Value) {
    if ($null -eq $Value) { return 'NULL' }
    if ($Value -is [datetime]) { $Value = $Value.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [datetimeoffset]) { $Value = $Value.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [bool]) { if ($Value) { return 'TRUE' } else { return 'FALSE' } }
    if ($Value -is [int] -or $Value -is [long]) { return $Value.ToString([Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [string] -and -not $Value.Contains([char]0)) { return "E'" + $Value.Replace('\', '\\').Replace("'", "''") + "'" }
    throw 'Unsupported database value.'
}
function Sql-Row([string]$Table, [string]$Columns, [object[]]$Values) {
    $literals = @(foreach ($value in $Values) { Sql-Literal $value })
    return "INSERT INTO $Table ($Columns) VALUES ($($literals -join ', '));"
}
$sourcePath = (Resolve-Path -LiteralPath $Source).ProviderPath
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if ($sourcePath -eq $outputPath -or (Test-Path -LiteralPath $outputPath)) { throw 'Output already exists. Move it to a private backup before rerunning.' }
$db = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($key in @('Devices','Sessions','Events')) {
    if ($null -eq $db.PSObject.Properties[$key] -or $db.$key -isnot [array]) { throw "Missing or invalid $key list." }
}
$lines = New-Object 'System.Collections.Generic.List[string]'
$codes = New-Object 'System.Collections.Generic.HashSet[string]'
$created = @{}
foreach ($code in $db.UsedSessionIds) { if ($null -ne $code) { [void]$codes.Add([string]$code) } }
$lines.Add(@'
-- Private RobLock history merge. No admin credentials or cached rack states imported.
DO $roblock_merge$
BEGIN
SET LOCAL TIME ZONE 'UTC';
LOCK TABLE roblock.devices, roblock.sessions, roblock.user_information, roblock.security_events, roblock.used_session_ids IN SHARE ROW EXCLUSIVE MODE;
CREATE TEMP TABLE merge_devices ON COMMIT DROP AS SELECT id, name FROM roblock.devices WITH NO DATA;
CREATE TEMP TABLE merge_sessions ON COMMIT DROP AS SELECT * FROM roblock.sessions WITH NO DATA;
CREATE TEMP TABLE merge_users ON COMMIT DROP AS SELECT * FROM roblock.user_information WITH NO DATA;
CREATE TEMP TABLE merge_events ON COMMIT DROP AS SELECT session_id, device_id, device_name, event_time, kind, title, detail FROM roblock.security_events WITH NO DATA;
CREATE TEMP TABLE merge_codes ON COMMIT DROP AS SELECT * FROM roblock.used_session_ids WITH NO DATA;
'@)
foreach ($d in $db.Devices) {
    $lines.Add((Sql-Row 'pg_temp.merge_devices' 'id,name' @($d.Id, $d.Name)))
}
foreach ($s in $db.Sessions) {
    $sid = [string]$s.Id
    if ($created.ContainsKey($sid)) { throw 'Duplicate local session ID.' }
    $created[$sid] = $s.CreatedAt
    [void]$codes.Add($sid)
    $lines.Add((Sql-Row 'pg_temp.merge_sessions' 'id,device_id,created_at,expires_at,claimed_at,ended_at,grace_until,end_reason' @($s.Id, $s.DeviceId, $s.CreatedAt, $s.ExpiresAt, $s.ClaimedAt, $s.EndedAt, $s.GraceUntil, $s.EndReason)))
    if ($null -ne $s.User) {
        $u = $s.User
        $lines.Add((Sql-Row 'pg_temp.merge_users' 'session_id,name,age,phone,address' @($sid, [string]$u.Name, [int]$u.Age, [string]$u.Phone, [string]$u.Address)))
    }
}
foreach ($e in $db.Events) {
    $lines.Add((Sql-Row 'pg_temp.merge_events' 'session_id,device_id,device_name,event_time,kind,title,detail' @($e.SessionId, [string]$e.DeviceId, [string]$e.DeviceName, $e.Time, [string]$e.Kind, [string]$e.Title, [string]$e.Detail)))
    if ($null -ne $e.SessionId) { [void]$codes.Add([string]$e.SessionId) }
}
foreach ($code in ($codes | Sort-Object)) {
    $lines.Add((Sql-Row 'pg_temp.merge_codes' 'session_id,issued_at' @($code, $created[$code])))
}
$lines.Add(@'
 IF EXISTS (SELECT 1 FROM pg_temp.merge_sessions WHERE ended_at IS NULL AND expires_at > now()) THEN
  RAISE EXCEPTION 'Merge stopped: an unexpired local session is still open. Finish it before taking a fresh backup.';
 END IF;
 IF EXISTS (SELECT 1 FROM pg_temp.merge_sessions l JOIN roblock.sessions c USING (id)
  WHERE ROW(l.device_id,l.created_at,l.expires_at,l.claimed_at,l.ended_at,l.grace_until,l.end_reason)
  IS DISTINCT FROM ROW(c.device_id,c.created_at,c.expires_at,c.claimed_at,c.ended_at,c.grace_until,c.end_reason)) THEN
  RAISE EXCEPTION 'Merge stopped: conflicting session records. Reconcile before importing.';
 END IF;
 IF EXISTS (SELECT 1 FROM pg_temp.merge_sessions l JOIN roblock.used_session_ids c ON c.session_id=l.id
  WHERE NOT EXISTS (SELECT 1 FROM roblock.sessions s WHERE s.id=l.id)) THEN
  RAISE EXCEPTION 'Merge stopped: local session ID is already reserved in cloud history.';
 END IF;
 IF EXISTS (SELECT 1 FROM pg_temp.merge_users l JOIN roblock.user_information c USING (session_id)
  WHERE ROW(l.name,l.age,l.phone,l.address) IS DISTINCT FROM ROW(c.name,c.age,c.phone,c.address)) THEN
  RAISE EXCEPTION 'Merge stopped: conflicting user records. Reconcile before importing.';
 END IF;
INSERT INTO roblock.devices (id,name)
 SELECT id,name FROM pg_temp.merge_devices ON CONFLICT (id) DO NOTHING;
INSERT INTO roblock.sessions (id,device_id,created_at,expires_at,claimed_at,ended_at,grace_until,end_reason)
 SELECT id,device_id,created_at,expires_at,claimed_at,ended_at,grace_until,end_reason FROM pg_temp.merge_sessions ON CONFLICT (id) DO NOTHING;
INSERT INTO roblock.user_information (session_id,name,age,phone,address)
 SELECT session_id,name,age,phone,address FROM pg_temp.merge_users ON CONFLICT (session_id) DO NOTHING;
INSERT INTO roblock.used_session_ids (session_id,issued_at)
 SELECT session_id,issued_at FROM pg_temp.merge_codes ON CONFLICT (session_id) DO NOTHING;
-- Keep the maximum multiplicity of identical events from both sources.
WITH incoming AS (
 SELECT *, row_number() OVER (PARTITION BY session_id,device_id,device_name,event_time,kind,title,detail) AS occurrence
 FROM pg_temp.merge_events
), existing AS (
 SELECT session_id,device_id,device_name,event_time,kind,title,detail,count(*) AS occurrences
 FROM roblock.security_events GROUP BY session_id,device_id,device_name,event_time,kind,title,detail
)
INSERT INTO roblock.security_events (session_id,device_id,device_name,event_time,kind,title,detail)
 SELECT i.session_id,i.device_id,i.device_name,i.event_time,i.kind,i.title,i.detail
 FROM incoming i WHERE i.occurrence > COALESCE((SELECT e.occurrences FROM existing e
  WHERE ROW(e.session_id,e.device_id,e.device_name,e.event_time,e.kind,e.title,e.detail)
  IS NOT DISTINCT FROM ROW(i.session_id,i.device_id,i.device_name,i.event_time,i.kind,i.title,i.detail)),0);
END;
$roblock_merge$;
'@)
$sql = $lines -join "`n"
# A random delimiter prevents a user-supplied text field from ending the SQL block.
$delimiter = '$roblock_' + [Guid]::NewGuid().ToString('N') + '$'
$header = 'DO $roblock_merge$'
$footer = '$roblock_merge$;'
$first = $sql.IndexOf($header, [StringComparison]::Ordinal)
$last = $sql.LastIndexOf($footer, [StringComparison]::Ordinal)
if ($first -lt 0 -or $last -le $first) { throw 'Invalid merge SQL block.' }
$sql = $sql.Substring(0, $first) + 'DO ' + $delimiter + $sql.Substring($first + $header.Length, $last - $first - $header.Length) + $delimiter + ';' + $sql.Substring($last + $footer.Length)
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $writer = [IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.WriteLine($sql) } finally { $writer.Dispose() }
} finally { $stream.Dispose() }
Write-Host 'Created roblock merge SQL. Keep it private.'
Write-Host "Local sessions: $($db.Sessions.Count); local events: $($db.Events.Count)"
