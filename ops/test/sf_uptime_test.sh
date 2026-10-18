#!/usr/bin/env bash
# No network, real credentials, journal, or host disk traversal is used.
set -uo pipefail
base=$(cd -- "$(dirname -- "$0")/.." && pwd)
script="$base/sf-uptime-check.sh"
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT
passed=0 total=0

setup() {
    case_dir="$tmp/$total"
    mkdir -p "$case_dir/bin" "$case_dir/state"
    export TEST_DIR="$case_dir"
    export PATH="$case_dir/bin:/usr/bin:/bin"
    export SF_UPTIME_URLS='https://test.invalid/health'
    export SF_UPTIME_STATE_DIR="$case_dir/state"
    export SF_UPTIME_CURL="$case_dir/bin/fake-curl"
    export SF_UPTIME_MAIL="$case_dir/bin/fake-mail"
    export SF_UPTIME_DF="$case_dir/bin/fake-df"
    unset SF_UPTIME_ENV_FILE SF_UPTIME_MAIL_DRY_RUN PYTHONPATH
    printf '500\n' >"$case_dir/code"
    printf '0\n' >"$case_dir/curl-exit"
    printf '20\n' >"$case_dir/disk"
    : >"$case_dir/mail"
    : >"$case_dir/log"
    cat >"$case_dir/bin/fake-curl" <<'SH'
#!/usr/bin/env bash
printf 'curl\n' >>"$TEST_DIR/calls"
cat "$TEST_DIR/code"
exit "$(cat "$TEST_DIR/curl-exit")"
SH
    cat >"$case_dir/bin/fake-mail" <<'SH'
#!/usr/bin/env bash
printf 'attempt\n' >>"$TEST_DIR/attempts"
cat >"$TEST_DIR/body.latest"
if [[ -f $TEST_DIR/mail-fail ]]; then
    printf 'Synthetic mail failure\n' >&2
    exit 1
fi
printf '%s\n' "$1" >>"$TEST_DIR/mail"
cat "$TEST_DIR/body.latest" >>"$TEST_DIR/bodies"
SH
    cat >"$case_dir/bin/fake-df" <<'SH'
#!/usr/bin/env bash
printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\n'
printf '/dev/fake 100 90 10 %s%% /\n' "$(cat "$TEST_DIR/disk")"
SH
    cat >"$case_dir/bin/logger" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"$TEST_DIR/log"
SH
    cat >"$case_dir/bin/systemctl" <<'SH'
#!/usr/bin/env bash
printf 'active\ninactive\nactive\n'
exit 3
SH
    cat >"$case_dir/bin/du" <<'SH'
#!/usr/bin/env bash
printf '1\t/a\n2\t/b\n3\t/c\n4\t/d\n5\t/e\n6\t/\n'
SH
    # The timeout itself is real; its child du is the stub above.
    chmod +x "$case_dir/bin/"*
}

run() { bash "$script" >"$case_dir/stdout" 2>"$case_dir/stderr"; }
mail_count() { awk 'END {print NR+0}' "$case_dir/mail"; }
contains() { awk -v needle="$2" 'index($0, needle) {found=1} END {exit !found}' "$1"; }
state_file() {
    local files=("$case_dir/state/"url-*.state)
    printf '%s\n' "${files[0]}"
}
check_alerted() {
    local n first sent last
    read -r n first sent last <"$(state_file)"
    [[ $sent == "$1" ]]
}

# NEDEN: Tek geçici HTTP hatası kullanıcıya gereksiz uyarı üretmemeli.
TekHata_UyariGondermez() {
    run
    [[ $(mail_count) == 0 ]]
    check_alerted 0
}

# NEDEN: İkinci hata uyarı üretmeli, üçüncü tur aynı kesintiyi yinelememeli.
IkiArdisikHata_TekUyariGonderir() {
    run; run; run
    [[ $(mail_count) == 1 ]]
    contains "$case_dir/mail" '[Wallet Mark] KESİNTİ: https://test.invalid/health'
    contains "$case_dir/bodies" 'HTTP: 500'
    contains "$case_dir/bodies" 'UTC'
    contains "$case_dir/bodies" 'TSİ (Europe/Istanbul)'
    contains "$case_dir/bodies" 'inactive'
    check_alerted 1
}

# NEDEN: Devam eden kesinti sessizce unutulmamalı; zaman dosyada geri alınır.
AltmisDakikaSonra_HatirlatmaGonderir() {
    run; run
    local file n first sent last
    file=$(state_file)
    read -r n first sent last <"$file"
    printf '%s %s %s %s\n' "$n" "$first" "$sent" "$((last-3601))" >"$file"
    run; run
    [[ $(mail_count) == 2 ]]
    contains "$case_dir/mail" '(hâlâ kapalı)'
}

# NEDEN: İyileşme yalnız önceden uyarılan kesintide ve yalnız bir kez bildirilir.
Duzelince_TekDuzeldiEpostasi() {
    run; run
    printf '200\n' >"$case_dir/code"
    run; run
    [[ $(mail_count) == 2 ]]
    contains "$case_dir/mail" '[Wallet Mark] DÜZELDİ:'
    contains "$case_dir/bodies" 'Kesinti süresi:'
    check_alerted 0
}

# NEDEN: Bildirilmemiş tek hatanın iyileşmesi e-posta üretmemeli.
UyariGonderilmedenDuzelirse_Eposta_Yok() {
    run
    printf '200\n' >"$case_dir/code"
    run
    [[ $(mail_count) == 0 ]]
    check_alerted 0
}

# NEDEN: %85 eşik değeri dahil olmalı; günlük kayıt başarısız gönderimde tutulmamalı.
Disk85_GundeBirUyari() {
    printf '200\n' >"$case_dir/code"
    printf '85\n' >"$case_dir/disk"
    touch "$case_dir/mail-fail"
    if run; then return 1; fi
    [[ ! -f $case_dir/state/disk.state ]]
    rm -- "$case_dir/mail-fail"
    run; run
    [[ $(mail_count) == 1 ]]
    contains "$case_dir/mail" '[Wallet Mark] DİSK %85'
    [[ $(cat "$case_dir/state/disk.state") == "$(date -u +%F)" ]]
    printf '2000-01-01\n' >"$case_dir/state/disk.state"
    run
    [[ $(mail_count) == 2 ]]
}

# NEDEN: SMTP hatası hem kesinti hem düzelme bildirimini sonraki tura bırakmalı.
EpostaHatasinda_DurumIsaretlenmez_SonrakiTurTekrarDener() {
    run
    touch "$case_dir/mail-fail"
    if run; then return 1; fi
    check_alerted 0
    contains "$case_dir/log" 'sf-uptime'
    rm -- "$case_dir/mail-fail"
    run; run
    [[ $(mail_count) == 1 ]]
    check_alerted 1
    printf '200\n' >"$case_dir/code"
    touch "$case_dir/mail-fail"
    if run; then return 1; fi
    check_alerted 1
    rm -- "$case_dir/mail-fail"
    run; run
    [[ $(mail_count) == 2 ]]
    check_alerted 0
}

# NEDEN: Gerçek SMTP ayrıştırma yolu boşluk ve '=' içeren sahte parolayı korumalı,
# bağlantı kurmamalı ve başarı/hata durumunda çıktıya veya journal'a sızdırmamalı.
Sir_Ciktiya_Yazilmaz() {
    local secret='FAKE_ONLY secret = with spaces'
    cat >"$case_dir/api.env" <<'CONFIG'
Email__SmtpHost=smtp.invalid
Email__SmtpPort=587
Email__SmtpUser=fake-user
Email__SmtpPassword=FAKE_ONLY secret = with spaces
Email__SmtpPassword=must-not-use-second-value
Email__FromAddress=fake@example.invalid
CONFIG
    mkdir "$case_dir/python"
    cat >"$case_dir/python/smtplib.py" <<'PY'
import os
assert os.environ['SF_SMTP_PASS'] == 'FAKE_ONLY secret = with spaces'
class SMTP:
    def __init__(self, *args, **kwargs):
        raise RuntimeError('Network forbidden in this test')
PY
    export PYTHONPATH="$case_dir/python"
    export SF_UPTIME_ENV_FILE="$case_dir/api.env"
    export SF_UPTIME_MAIL_DRY_RUN=1
    unset SF_UPTIME_MAIL
    run; run
    check_alerted 1
    cat "$case_dir/stdout" "$case_dir/stderr" "$case_dir/log" >"$case_dir/all-output"
    if contains "$case_dir/all-output" "$secret"; then return 1; fi
    # Force a configuration error through the same real-mail path (still no SMTP).
    awk '{if (index($0, "Email__SmtpPort=")==1) print "Email__SmtpPort=invalid"; else print}' \
        "$case_dir/api.env" >"$case_dir/api.env.tmp"
    mv -- "$case_dir/api.env.tmp" "$case_dir/api.env"
    local file n first sent last
    file=$(state_file)
    read -r n first sent last <"$file"
    printf '%s %s %s %s\n' "$n" "$first" "$sent" "$((last-3601))" >"$file"
    if run; then return 1; fi
    cat "$case_dir/stdout" "$case_dir/stderr" "$case_dir/log" >>"$case_dir/all-output"
    if contains "$case_dir/all-output" "$secret"; then return 1; fi
    contains "$case_dir/log" 'E-posta gönderilemedi'
}

# NEDEN: curl 200 yazsa bile sıfır olmayan çıkış kodu hatadır; URL sayaçları ayrıdır.
CurlHatasi_VeUrlSayaclari_Bagimsiz() {
    export SF_UPTIME_URLS='https://one.invalid/ https://two.invalid/'
    printf '200\n' >"$case_dir/code"
    printf '7\n' >"$case_dir/curl-exit"
    run; run; run
    [[ $(mail_count) == 2 ]]
    contains "$case_dir/bodies" 'curl başarılı: 0'
    printf '0\n' >"$case_dir/curl-exit"
    run; run
    [[ $(mail_count) == 4 ]]
}

for scenario in \
    TekHata_UyariGondermez \
    IkiArdisikHata_TekUyariGonderir \
    AltmisDakikaSonra_HatirlatmaGonderir \
    Duzelince_TekDuzeldiEpostasi \
    UyariGonderilmedenDuzelirse_Eposta_Yok \
    Disk85_GundeBirUyari \
    EpostaHatasinda_DurumIsaretlenmez_SonrakiTurTekrarDener \
    Sir_Ciktiya_Yazilmaz \
    CurlHatasi_VeUrlSayaclari_Bagimsiz; do
    total=$((total+1))
    # Do not put this subshell in an if: that would disable errexit in assertions.
    (set -e; setup; "$scenario") >"$tmp/scenario-output" 2>&1
    status=$?
    if (( status == 0 )); then
        passed=$((passed+1))
        printf 'GEÇTİ: %s\n' "$scenario"
    else
        printf 'KALDI: %s (kod %s)\n' "$scenario" "$status"
        cat "$tmp/scenario-output"
    fi
done
printf '%s/%s geçti\n' "$passed" "$total"
[[ $passed == "$total" ]]
