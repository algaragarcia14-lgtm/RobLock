-- Private RobLock history merge. No admin credentials or cached rack states imported.
DO $roblock_b941968ee3ad4323b1da01f7fae27826$
BEGIN
SET LOCAL TIME ZONE 'UTC';
LOCK TABLE roblock.devices, roblock.sessions, roblock.user_information, roblock.security_events, roblock.used_session_ids IN SHARE ROW EXCLUSIVE MODE;
CREATE TEMP TABLE merge_devices ON COMMIT DROP AS SELECT id, name FROM roblock.devices WITH NO DATA;
CREATE TEMP TABLE merge_sessions ON COMMIT DROP AS SELECT * FROM roblock.sessions WITH NO DATA;
CREATE TEMP TABLE merge_users ON COMMIT DROP AS SELECT * FROM roblock.user_information WITH NO DATA;
CREATE TEMP TABLE merge_events ON COMMIT DROP AS SELECT session_id, device_id, device_name, event_time, kind, title, detail FROM roblock.security_events WITH NO DATA;
CREATE TEMP TABLE merge_codes ON COMMIT DROP AS SELECT * FROM roblock.used_session_ids WITH NO DATA;
INSERT INTO pg_temp.merge_devices (id,name) VALUES (E'rack-a1', E'Rack A1');
INSERT INTO pg_temp.merge_devices (id,name) VALUES (E'rack-a2', E'Rack A2');
INSERT INTO pg_temp.merge_devices (id,name) VALUES (E'rack-b1', E'Rack B1');
INSERT INTO pg_temp.merge_devices (id,name) VALUES (E'rack-b2', E'Rack B2');
INSERT INTO pg_temp.merge_sessions (id,device_id,created_at,expires_at,claimed_at,ended_at,grace_until,end_reason) VALUES (E'10326', E'rack-a1', E'2026-11-25T13:05:34.6799106Z', E'2026-11-25T15:05:34.6799106Z', NULL, E'2026-11-25T13:07:04.0291922Z', NULL, E'ended by admin');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:05:34.7066914Z', E'info', E'Session created', E'QR code generated');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:05:39.8373325Z', E'ok', E'Locked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:05:39.8403219Z', E'info', E'Grace period started', E'15-second session-retention window after confirmed lock');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:01.1198968Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:09.6188377Z', E'ok', E'Unlocked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:13.2335272Z', E'ok', E'Locked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:13.2335336Z', E'info', E'Grace period started', E'15-second session-retention window after confirmed lock');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:38.2184518Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:53.1503695Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:06:57.7710196Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:07:04.0129993Z', E'ok', E'Unlocked', E'By admin (admin); Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (E'10326', E'rack-a1', E'Rack A1', E'2026-11-25T13:07:04.0291946Z', E'info', E'Session ended', E'Ended by admin (admin)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:08.7594491Z', E'ok', E'Locked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:13.5911379Z', E'warn', E'Arduino command not confirmed', E'Arduino REJECTED: reason=ALARM_ACTIVE');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:17.2873756Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:19.2484194Z', E'warn', E'Arduino command not confirmed', E'Arduino REJECTED: reason=ALARM_RECOVERY');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:27.268139Z', E'ok', E'Unlocked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:31.2449684Z', E'ok', E'Locked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:38.6157503Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:44.5029857Z', E'ok', E'Unlocked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:07:48.2888995Z', E'ok', E'Locked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:08:00.3246405Z', E'ok', E'Tamper alarm acknowledged', E'By admin; Arduino confirmed; automatic 2-second rearm');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:08:02.1555549Z', E'warn', E'Arduino command not confirmed', E'Arduino REJECTED: reason=ALARM_RECOVERY');
INSERT INTO pg_temp.merge_events (session_id,device_id,device_name,event_time,kind,title,detail) VALUES (NULL, E'rack-a1', E'Rack A1', E'2026-11-25T13:08:08.5584891Z', E'ok', E'Unlocked', E'Arduino command completed (position not verified)');
INSERT INTO pg_temp.merge_codes (session_id,issued_at) VALUES (E'10326', E'2026-11-25T13:05:34.6799106Z');
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
$roblock_b941968ee3ad4323b1da01f7fae27826$;
