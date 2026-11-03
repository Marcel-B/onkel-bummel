#!/usr/bin/env bash
# Asks for the App Store Connect values and stores them as GitHub secrets for
# .github/workflows/testflight.yml. Needs the GitHub CLI (`gh auth login`).
set -euo pipefail

repo="${REPO:-Marcel-B/onkel-bummel}"

command -v gh >/dev/null || { echo "GitHub CLI fehlt: brew install gh" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Erst anmelden: gh auth login" >&2; exit 1; }

echo "TestFlight-Secrets für $repo"
echo "Werte stehen in App Store Connect unter Benutzer und Zugriff > Integrationen > App Store Connect API"
echo "Ein Teamschlüssel (Rolle App Manager) gilt für alle Apps des Teams, auch den von Slipways."
echo

read -rp "Pfad zur .p8-Datei (AuthKey_XXXXXXXXXX.p8): " key_path
key_path="${key_path/#\~/$HOME}"
[ -f "$key_path" ] || { echo "Datei nicht gefunden: $key_path" >&2; exit 1; }
grep -q "BEGIN PRIVATE KEY" "$key_path" || { echo "Das sieht nicht nach einem .p8-Schlüssel aus." >&2; exit 1; }

guess_key_id=""
if [[ "$(basename "$key_path")" =~ ^AuthKey_([A-Z0-9]+)\.p8$ ]]; then
  guess_key_id="${BASH_REMATCH[1]}"
fi
read -rp "Key ID${guess_key_id:+ [$guess_key_id]}: " key_id
key_id="${key_id:-$guess_key_id}"
[[ "$key_id" =~ ^[A-Z0-9]{10}$ ]] || { echo "Key ID hat 10 Zeichen (A-Z, 0-9)." >&2; exit 1; }

read -rp "Issuer ID (UUID): " issuer_id
[[ "$issuer_id" =~ ^[0-9a-fA-F-]{36}$ ]] || { echo "Issuer ID ist eine UUID mit 36 Zeichen." >&2; exit 1; }

echo
echo "Trage ein: APP_STORE_CONNECT_KEY_ID, APP_STORE_CONNECT_ISSUER_ID, APP_STORE_CONNECT_KEY"
read -rp "Fortfahren? [j/N] " ok
[[ "$ok" =~ ^[jJyY]$ ]] || exit 0

gh secret set APP_STORE_CONNECT_KEY_ID --repo "$repo" --body "$key_id"
gh secret set APP_STORE_CONNECT_ISSUER_ID --repo "$repo" --body "$issuer_id"
base64 < "$key_path" | tr -d '\n' | gh secret set APP_STORE_CONNECT_KEY --repo "$repo"

echo
echo "Fertig. Ersten Upload starten: gh workflow run testflight.yml --repo $repo"
