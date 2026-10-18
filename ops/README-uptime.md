# Wallet Mark kesinti ve disk uyarıları

Kullanıcı onayı: 18.10.2026. Ubuntu 24.04 üzerinde root olarak çalışır.
İki ardışık başarısız kontrolde kesinti, 60 dakika arayla hatırlatma ve
iyileşmede bir düzelme e-postası gönderir. Başlangıç zamanı ilk başarısız
kontroldür; gerçek kesinti süresi ölçüm aralığı nedeniyle yaklaşık değerdir.
İlk uyarı hatanın zamanlamasına göre yaklaşık 5–10 dakika sonra ulaşır.
Disk `/` %85 ve üzerindeyken UTC takvim günü başına en fazla bir uyarı gönderir.

Kurulum (repo kökünde, sunucuda root olarak):

```bash
bash ops/test/sf_uptime_test.sh
install -m 0755 ops/sf-uptime-check.sh /usr/local/bin/sf-uptime-check.sh
install -m 0644 ops/sf-uptime.service /etc/systemd/system/sf-uptime.service
install -m 0644 ops/sf-uptime.timer /etc/systemd/system/sf-uptime.timer
systemctl daemon-reload
systemctl enable --now sf-uptime.timer
systemctl list-timers sf-uptime.timer
journalctl -u sf-uptime.service -t sf-uptime --since today
```

Gereksinimler: bash, curl, python3, grep, coreutils, util-linux (flock),
systemd ve tzdata. SMTP ayarları `/etc/smartfinance/api.env` dosyasından
`grep | head -1 | cut -d= -f2-` ile alınır; dosya source edilmez.
Bu dosyayı yalnız root okuyabilmelidir. SMTP STARTTLS ve sertifika doğrulaması
kullanılır. Sırlar yalnız Python alt sürecine ortam değişkenleriyle aktarılır;
betiği `bash -x` veya başka bir izleme aracıyla çalıştırmayın.

Varsayılan alıcı `mehmet.koc.brs1453@gmail.com`; doğrudan çağrıda ilk argümanla
değiştirilebilir. Timer için gerekirse `systemctl edit sf-uptime.service` ile:

```ini
[Service]
ExecStart=
ExecStart=/usr/local/bin/sf-uptime-check.sh baska@example.com
```

Durum `/var/lib/sf-uptime/` altında 0700 dizin ve 0600 dosyalarda tutulur.
URL dosyaları `hata_sayısı ilk_hata_epoch uyarıldı son_gönderim_epoch`
alanlarını içerir; `disk.state` son başarılı bildirimin UTC günüdür.
Yazımlar aynı dizinde geçici dosya ve rename ile yapılır; flock eşzamanlı
çalışmayı önler. SMTP hatası journal'a genel hata yazar ve exit 1 döner;
başarısız gönderim kaydedilmez. SMTP kabulünden hemen sonra süreç öldürülürse,
sonraki turda mükerrer gönderim mümkün olabilir.

Testler gerçek ağ, disk taraması, journal veya sır dosyası kullanmaz.
`SF_UPTIME_URLS` boşlukla ayrılmış URL listesidir. `SF_UPTIME_STATE_DIR`,
`SF_UPTIME_CURL`, `SF_UPTIME_MAIL`, `SF_UPTIME_DF` bağımlılıkları değiştirir.
Komut değişkenleri tek çalıştırılabilir dosya yoludur; ek argüman içermez.
Mail komutu konuyu ilk argüman, gövdeyi stdin üzerinden alır; başarı exit 0'dır.
Yalnız sentetik SMTP testi için `SF_UPTIME_ENV_FILE` sahte ayar dosyası seçer,
`SF_UPTIME_MAIL_DRY_RUN=1` SMTP bağlantısını tamamen atlar. Bunları üretim
servisine eklemeyin: kuru çalışma başarılı gönderim olarak kaydedilir.
Test ayrıca smtplib yerine bağlantı kurmayı reddeden sahte modül kullanır.

Bu izleyici aynı sunucudadır: sunucunun tamamı kapandığında e-posta gönderemez.
