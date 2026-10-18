#!/usr/bin/env bash
# Wallet Mark: no tracing; SMTP credentials must never reach stdout/journal.
set -euo pipefail
umask 077

recipient=${1:-mehmet.koc.brs1453@gmail.com}
state_dir=${SF_UPTIME_STATE_DIR:-/var/lib/sf-uptime}
urls=${SF_UPTIME_URLS:-https://api.walletmark.com.tr/health https://walletmark.com.tr/}
curl_cmd=${SF_UPTIME_CURL:-curl}
df_cmd=${SF_UPTIME_DF:-df}
mkdir -p -- "$state_dir"
chmod 0700 -- "$state_dir"
# util-linux flock is shipped with Ubuntu. Keep the descriptor until exit.
exec 9>"$state_dir/lock"
flock -n 9 || exit 0

log_error() {
    printf '%s\n' "$1" >&2
    logger -t sf-uptime -- "$1" || true
}

send_mail() {
    local subject=$1 body=$2 host port user pass sender config
    if [[ -n ${SF_UPTIME_MAIL:-} ]]; then
        if printf '%s\n' "$body" | "$SF_UPTIME_MAIL" "$subject" >/dev/null 2>&1; then
            return 0
        fi
    else
        # These two extra switches are for the synthetic-credentials dry-run test.
        config=${SF_UPTIME_ENV_FILE:-/etc/smartfinance/api.env}
        if host=$(grep '^Email__SmtpHost=' "$config" 2>/dev/null | head -1 | cut -d= -f2-) &&
           port=$(grep '^Email__SmtpPort=' "$config" 2>/dev/null | head -1 | cut -d= -f2-) &&
           user=$(grep '^Email__SmtpUser=' "$config" 2>/dev/null | head -1 | cut -d= -f2-) &&
           pass=$(grep '^Email__SmtpPassword=' "$config" 2>/dev/null | head -1 | cut -d= -f2-) &&
           sender=$(grep '^Email__FromAddress=' "$config" 2>/dev/null | head -1 | cut -d= -f2-); then
            if SF_SMTP_HOST="$host" SF_SMTP_PORT="$port" SF_SMTP_USER="$user" \
               SF_SMTP_PASS="$pass" SF_SMTP_FROM="$sender" SF_MAIL_TO="$recipient" \
               SF_MAIL_SUBJECT="$subject" SF_MAIL_BODY="$body" \
               SF_MAIL_DRY_RUN="${SF_UPTIME_MAIL_DRY_RUN:-0}" python3 - <<'PY' >/dev/null 2>&1
import os
import smtplib
import ssl
from email.message import EmailMessage

try:
    msg = EmailMessage()
    msg['From'] = os.environ['SF_SMTP_FROM']
    msg['To'] = os.environ['SF_MAIL_TO']
    msg['Subject'] = os.environ['SF_MAIL_SUBJECT']
    msg.set_content(os.environ['SF_MAIL_BODY'])
    port = int(os.environ['SF_SMTP_PORT'])
    if not all(os.environ[k] for k in ('SF_SMTP_HOST', 'SF_SMTP_USER',
                                      'SF_SMTP_PASS', 'SF_SMTP_FROM')):
        raise ValueError('Missing configuration')
    if os.environ['SF_MAIL_DRY_RUN'] != '1':
        with smtplib.SMTP(os.environ['SF_SMTP_HOST'], port, timeout=10) as smtp:
            smtp.starttls(context=ssl.create_default_context())
            smtp.login(os.environ['SF_SMTP_USER'], os.environ['SF_SMTP_PASS'])
            smtp.send_message(msg)
except Exception:
    # Never print exceptions: SMTP errors can include credentials/server data.
    raise SystemExit(1)
PY
            then
                return 0
            fi
        fi
    fi
    log_error 'E-posta gönderilemedi; sonraki turda tekrar denenecek.'
    return 1
}

stamp() {
    date -u -d "@$1" '+%Y-%m-%d %H:%M:%S UTC'
    TZ=Europe/Istanbul date -d "@$1" '+%Y-%m-%d %H:%M:%S TSİ (Europe/Istanbul)'
}

save_url() {
    printf '%s %s %s %s\n' "$count" "$first" "$alerted" "$last" >"$file.tmp"
    mv -f -- "$file.tmp" "$file"
}

now=$(date -u +%s)
result=0
for url in $urls; do
    key=$(printf '%s' "$url" | sha256sum)
    file="$state_dir/url-${key%% *}.state"
    count=0 first=0 alerted=0 last=0
    if [[ -f $file ]]; then
        read -r count first alerted last <"$file"
        if [[ ! $count =~ ^[0-9]+$ || ! $first =~ ^[0-9]+$ ||
              ! $alerted =~ ^[01]$ || ! $last =~ ^[0-9]+$ ]]; then
            log_error 'Geçersiz URL durum dosyası.'
            exit 1
        fi
    fi
    code=000
    curl_ok=0
    if code=$("$curl_cmd" --max-time 10 -s -o /dev/null -w '%{http_code}' "$url" 2>/dev/null); then
        curl_ok=1
    fi
    [[ $code =~ ^[0-9]{3}$ ]] || code=000
    if [[ $curl_ok == 1 && $code == 200 ]]; then
        if (( alerted )); then
            body=$(printf 'URL: %s\nHTTP: %s\nİlk hata:\n%s\nKesinti süresi: %s saniye\n' "$url" "$code" "$(stamp "$first")" "$((now-first))")
            if ! send_mail "[Wallet Mark] DÜZELDİ: $url" "$body"; then
                result=1
                continue
            fi
        fi
        count=0 first=0 alerted=0 last=0
    else
        (( count == 0 )) && first=$now
        count=$((count+1))
        if (( count >= 2 && (alerted == 0 || now-last >= 3600) )); then
            services=$(systemctl is-active smartfinance-api mssql-server caddy 2>/dev/null) || true
            body=$(printf 'URL: %s\nHTTP: %s (curl başarılı: %s)\nİlk hata:\n%s\nServisler (smartfinance-api, mssql-server, caddy sırasıyla):\n%s\n' "$url" "$code" "$curl_ok" "$(stamp "$first")" "$services")
            subject="[Wallet Mark] KESİNTİ: $url"
            (( alerted == 0 )) || subject="[Wallet Mark] KESİNTİ: $url (hâlâ kapalı)"
            if send_mail "$subject" "$body"; then
                alerted=1 last=$now
            else
                result=1
            fi
        fi
    fi
    save_url
done

if disk_output=$("$df_cmd" -P / 2>/dev/null) &&
   disk=$(printf '%s\n' "$disk_output" | awk 'NR==2 {gsub(/%/, "", $5); print $5}'); then
    if [[ ! $disk =~ ^[0-9]+$ ]]; then
        log_error 'Disk kullanımı okunamadı.'
        result=1
    elif (( disk >= 85 )); then
        today=$(date -u +%F)
        disk_day=''
        if [[ -f $state_dir/disk.state ]]; then
            read -r disk_day <"$state_dir/disk.state"
        fi
        if [[ $disk_day != "$today" ]]; then
            if ! largest=$(timeout 30 du -x -d2 / 2>/dev/null | sort -h | tail -n 5); then
                largest='Dizin boyutları alınamadı veya 30 saniyelik zaman aşımı doldu.'
            fi
            body=$(printf '/ kullanımı: %%%s\nEn büyük 5 dizin (bayt değil, du varsayılan blok birimi):\n%s\n' "$disk" "$largest")
            if send_mail "[Wallet Mark] DİSK %$disk" "$body"; then
                printf '%s\n' "$today" >"$state_dir/disk.state.tmp"
                mv -f -- "$state_dir/disk.state.tmp" "$state_dir/disk.state"
            else
                result=1
            fi
        fi
    fi
else
    log_error 'Disk kullanımı okunamadı.'
    result=1
fi
exit "$result"
