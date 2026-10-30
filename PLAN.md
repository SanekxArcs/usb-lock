# USB Lock implementation plan

Independent implementation based on the six supplied USB Raptor screenshots. WPF-UI/Mica visual toolkit matching ../cursor-lang.

1. Core: removable drives, random USB keys, local trust, verification, arm/disarm, removal countdown, native Windows lock.
2. Reliability: exact-key checking, optional formatted-volume serial binding, revocation, atomic DPAPI settings, session/resume handling, single instance.
3. Convenience: tray, manual lock, cancel countdown, startup/minimized/auto-arm, notifications/sounds, themes, history, preferences import/export.
4. Verification: deterministic core checks, release build, UI inspection, runnable release folder, feature report.

Deferred: automatic Windows login/custom lock screen, backdoor/master keys, network/Telegram/serial control, arbitrary command execution, muting, drive rename. No placeholder settings.

Native Windows lock requires Windows Hello/password to sign back in. Reinsertion cancels a pending lock. The app is not an anti-tamper service. Volume serial is not hardware identity or clone resistance. Defaults are disarmed; startup changes are opt-in.


## 0.2.0 — trusted USB peripherals

Add present physical USB PnP discovery using SetupAPI (including composite-camera names); exact instance-ID trust in the existing DPAPI store with legacy defaults; a second setup mode; shared countdown/arm/revoke/auto-arm flows. Verify identity substitution, legacy JSON, live enumeration and WPF renders. Publish side by side without stopping active protection. Do not open camera streams or manipulate hardware during verification.
