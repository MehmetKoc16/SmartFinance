#!/usr/bin/env bash
# Wallet Mark API'yi kesintiyi en aza indirerek yayinlar. Sunucuda
# /usr/local/bin/sf-deploy.sh olarak durur; kaynak /opt/smartfinance-src.
#
# Eskiden yayin calisan klasorun USTUNE yapiliyordu: dosyalar kilitli oldugu
# icin once servis durduruluyordu ve API 20-30 sn kapali kaliyordu (26.09.2026,
# testci arama yaparken bos sonuc aldi). Simdi:
#   1. yeni surum eski calisirken AYRI klasore yayinlanir,
#   2. /var/www/smartfinance baglantisi (symlink) tek adimda yeni klasore doner,
#   3. servis yeniden baslar — kesinti yalnizca acilis suresi (birkac saniye),
#   4. saglik kontrolu gecmezse onceki surume otomatik geri donulur.
#
# Veritabani migration'i BU BETIGIN ISI DEGIL: once sf-backup.sh, sonra
# `dotnet ef database update` elle calistirilir.
set -euo pipefail

KAYNAK=/opt/smartfinance-src
SURUMLER=/var/www/sf-releases
CANLI=/var/www/smartfinance
SAKLANAN=3

yeni="$SURUMLER/$(date +%Y%m%d-%H%M%S)"
onceki="$(readlink -f "$CANLI")"

echo "Yayinlaniyor: $yeni"
dotnet publish "$KAYNAK/SmartFinance.API" -c Release -o "$yeni" --nologo -v quiet
chown -R smartfinance:smartfinance "$yeni"

gecis() {  # baglantiyi tek adimda (atomik) degistir
    ln -sfn "$1" "$CANLI.yeni"
    mv -Tf "$CANLI.yeni" "$CANLI"
}

saglikli() {
    for _ in $(seq 1 30); do
        curl -fsS -o /dev/null --max-time 2 http://127.0.0.1:5059/health && return 0
        sleep 1
    done
    return 1
}

gecis "$yeni"
baslangic=$(date +%s%N)
systemctl restart smartfinance-api
if saglikli; then
    echo "Canli: $yeni ($(( ($(date +%s%N) - baslangic) / 1000000 )) ms icinde saglikli)"
else
    echo "HATA: yeni surum saglikli degil, geri donuluyor: $onceki" >&2
    gecis "$onceki"
    systemctl restart smartfinance-api
    saglikli && echo "Onceki surum yeniden canli." >&2
    exit 1
fi

# Eski surumleri temizle (canli olan ve bir onceki her zaman kalir).
ls -1dt "$SURUMLER"/*/ | tail -n +$((SAKLANAN + 1)) | while read -r d; do
    d="${d%/}"
    [ "$d" = "$yeni" ] || [ "$d" = "$onceki" ] || rm -rf "$d"
done
