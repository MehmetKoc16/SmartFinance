# walletmark.com.tr (statik site)

`<sunucu>`: sunucu adresi; repoya yazılmaz, yerel SSH yapılandırmasından alınır.

Ana sayfa, gizlilik politikasi, hesap silme ve sifre sifirlama sayfalari.
Play Console gizlilik politikasi ve hesap silme icin herkese acik URL istiyor.

Sunucuda `/var/www/walletmark-site/` altinda, Caddy dogrudan sunar (sahibi
`caddy:caddy`, 644). Bu klasor o dizinin kaynagidir; degisiklik once burada
yapilir, sonra kopyalanir:

    scp site/*.html site/style.css root@<sunucu>:/var/www/walletmark-site/

Gizlilik politikasi degisirse Play Console > Veri guvenligi formu ile tutarli
kalmali ve "Son guncelleme" tarihi degistirilmeli.
