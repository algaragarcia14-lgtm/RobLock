# RobLock

C# back end (ASP.NET Core) with plain HTML pages for the front end.

## Run it (Windows)

1. Install the .NET 8 SDK or newer from https://dotnet.microsoft.com/download
2. Open a terminal in this folder (the one with `RobLock.csproj`)
3. `dotnet run`
4. Open http://localhost:5000

The first `dotnet run` downloads one package (QRCoder), so it needs internet once.

## First time

Open http://localhost:5000/admin.html. The first time, it asks you to choose a username and a
password (8 or more characters). That is the only account on the site. After that it is a normal
login with both. An older admin that only had a password is reset automatically: the setup screen
shows again, and your sessions and history are kept.

## How it works

1. Admin logs in and opens the dashboard.
2. Generate new session: pick a device, and the server makes a random 5-digit session id
   and shows it as a QR code (it holds a link like `.../control.html?s=48213`).
3. The user scans the QR code, either with the Scan QR button on the home page or with
   their phone's camera app. They land on a page with one Lock/Unlock button, plus a small "Your details"
   form (name, age, phone number, address). The admin sees those details on the session in the
   History column and on the device card. They are optional and can be edited until the session ends.
4. The dashboard shows it all: History (sessions with time in and time out, plus events),
   Generate new session, and Devices (active QR, lock/unlock, buzzer).

Sessions last 2 hours, or until the admin ends them or the user successfully unlocks outside the grace period (change
`SessionMinutes` in `Program.cs`). A new confirmed website lock starts a 15-second grace period (`GraceSeconds`).
An authorized user unlock processed by the server before the deadline keeps the same session open after confirmation;
a subsequent lock starts a new grace period. Unlock at or after the deadline ends the session and invalidates its QR.
The timer is stored on the server and survives page reloads. Repeated simulated lock commands while already locked do not extend it.
For a fresh website lock with an active session PIN, the Arduino starts its own 15-second grace timer when the mechanism completes its commanded movement. The tamper alarm does not activate during that window; completed sensor pulses during grace are discarded rather than carried over into an alarm. A sensor that is still active at expiry or new tampering after expiry triggers the normal alarm. A successful authorized unlock ends the grace state. The firmware timer operates offline and status/PIN polling cannot restart it. The server uses the reported remaining time. Movement filtering and 2-second alarm recovery are unchanged. Keypad commands retain their
existing behavior; they do not initiate a website grace period. Admin End session always ends the session after unlocking;
if the rack is already confirmed unlocked, it closes without a redundant unlock operation.
A physical rack must report the expected commanded state; if the Arduino rejects the command
or cannot confirm it, the explicit end request fails and the session remains active (subject to
normal expiration). Existing alarm, keypad and remote unlock permission checks are unchanged.
Automatic two-hour expiration remains unchanged and does not send a physical unlock command.
One device can have one active session at a time. Issued five-digit codes are retained separately
in `data/db.json` to prevent reuse after history trimming; older codes already removed before
this update cannot be recovered. The five-digit format has at most 90,000 unique codes.

## Using phones

- Start the server so it can be reached on your network:
  `dotnet run --urls http://0.0.0.0:5000`
  Then open the dashboard using this PC's address, for example http://192.168.1.20:5000,
  so the QR codes point somewhere a phone can reach. Allow it through Windows Firewall if asked.
- The phone's own camera app can open the QR link over plain http.
- The Scan QR button shows the live camera on https or localhost. Browsers block the live camera
  on plain http, so there the button opens the phone's camera app instead: take a photo of the
  QR code and the page reads it from the photo. The same photo fallback is used if camera
  access is blocked or no camera is found.

## About the QR reader

`wwwroot/js/jsQR.js` is the jsQR library (Apache 2.0 license in `jsQR.LICENSE.txt`). It is
included in the project so scanning works without internet. Android phones use their built-in
QR reader when they have one, and everything else uses jsQR.

## Notes

- Data is stored in `data/db.json` (created on first run). Delete that file to start fresh.
  User details are stored there in plain text, so keep that file private.
- Devices are simulated by default (Rack A1, A2, B1, B2). To attach an Arduino Uno R3,
  connect it by USB and configure **both** server environment variables before `dotnet run`:
  `ROBLOCK_ARDUINO_DEVICE_ID=rack-a1` and `ROBLOCK_SERIAL_PORT=COM3` (replace COM3 with
  the actual port shown in Device Manager). On PowerShell use `$env:ROBLOCK_ARDUINO_DEVICE_ID="rack-a1"`
  and `$env:ROBLOCK_SERIAL_PORT="COM3"`. Other devices remain simulated. Arduino uses 115200 baud.
  Only a `COMPLETED` reply records a successful command. The displayed state is the Arduino's
  *commanded* state, not a physical lock-position measurement. The keypad and alarm remain local;
  the admin can start or stop a five-second buzzer test from the dashboard. During a confirmed
  tamper alarm, the same button becomes Acknowledge alarm (admin only). Arduino must confirm
  that its commanded lock state is still LOCKED, its buzzer is off, and its arm state is SILENCED.
  The mechanism is not moved, and the Arduino automatically rearms after two seconds; new
  tamper activity during recovery causes a new alarm at the end of that period. Keypad PIN
  acknowledgment remains available and uses the same recovery duration. Acknowledgment does not
  change keypad lockout or unlock permissions. Upload the matching updated sketch before using
  the button. Serial connection issues are reported as errors and may leave
  the commanded state and buzzer status unknown.
- Five digits is easy to guess, so wrong tries are limited to 12 per minute per address.

### Session keypad PIN
The physical Arduino keypad uses the current rack's five-digit session ID, not a fixed password.
The server provisions it when a session is created, synchronizes it on status checks and lock/unlock commands,
and clears it on explicit session completion. The firmware rejects PIN authentication after its local
remaining-lifetime timer expires, even offline. A reset clears the PIN until the server synchronizes again.
Existing sessions will synchronize on the next successful physical status request. No PIN is printed to
serial status or added to log details. The existing keypad attempt limit and alarm handling are unchanged.
If PIN removal cannot be confirmed after an unlock, the session remains active; the admin can retry End session.
Local keypad commands still have their original behavior; the website's 15-second grace/one-time session rules
apply to website commands, not keypad unlocks. This is a classroom prototype: a five-digit QR ID is a bearer
credential and must be kept private; it is not suitable as the sole access credential for production security.
No session means no local PIN authentication; use the existing admin dashboard to manage the rack.
