# Veri İhlali Müdahale Kontrol Listesi (Wallet Mark)

> Bu bir operasyon kontrol listesidir; hukuki değerlendirme yerine geçmez.

Bu belgeye sır, parola, anahtar değeri, IP adresi veya kişi verisi YAZILMAZ.
Olay sırasında tutulan notlar repoya değil, olay günlüğüne yazılır.

## 1. İlk saat: tespit ve kayıt

- [ ] Olay günlüğünü aç (repo dışında, erişimi kısıtlı bir dosya). İlk satır:
      olay numarası, açılış saati (TSİ).
- [ ] Şunları yaz: **ne** görüldü, **ne zaman** (ilk belirti ve fark edilme saati
      ayrı ayrı), **kim** fark etti, **nasıl** fark edildi (uyarı, kullanıcı
      bildirimi, log, üçüncü kişi).
- [ ] Bundan sonraki her adımı saatiyle günlüğe ekle (kim, ne yaptı, sonuç).
- [ ] 72 saatlik süreyi başlat: süre, ihlalin **öğrenildiği** andan işler.
      Bitiş saatini günlüğün en üstüne yaz.

## 2. Sızıntıyı durdurma

Önce kanıtın kaybolmayacağından emin ol (bkz. 3. bölüm: servis loglarını
yeniden başlatmadan önce kopyala), sonra:

- [ ] **Gerekirse API'yi kapat.** v16'da hazır bir bakım modu yok. Seçenekler:
      `systemctl stop smartfinance-api` (uygulama hata gösterir) ya da Caddy'de
      API yolları için geçici 503 yanıtı. Statik site açık kalabilir.
- [ ] **Anahtarları döndür.** Hepsi sunucudaki ortam dosyasında tutulur; yeni
      değeri yaz, eskisini geçersiz kıl, servisi yeniden başlat:
  - [ ] JWT imza anahtarı (değişince tüm erişim token'ları geçersiz olur)
  - [ ] Veritabanı parolası (SQL Server kullanıcısı)
  - [ ] Firebase servis hesabı anahtarı (Firebase konsolunda eskisini sil)
  - [ ] Google Play servis hesabı anahtarı (Google Cloud konsolunda eskisini sil)
  - [ ] SMTP parolası (e-posta sağlayıcısının panelinde)
  - [ ] Yedek şifreleme parolası (sonraki yedekler yeni parolayla; eski yedekleri
        açmak için eski parolayı güvenli yerde ayrıca sakla)
- [ ] **Tüm oturumları kapat.** Tüm refresh token'ları iptal et:
      `UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE RevokedAt IS NULL;`
      JWT anahtarı da döndürülürse kullanıcılar yeniden giriş yapmak zorunda kalır.
- [ ] Sunucuya erişimi olan hesapları gözden geçir (SSH anahtarları, sudo
      kullanıcıları); tanımadığın anahtarı kaldır.
- [ ] Açığın kendisi biliniyorsa (hatalı uç, sızan dosya) kapatılana kadar API'yi
      açma.

## 3. Kanıt toplama

Kural: kanıtı **değiştirmeden kopyala**, kopyanın özetini (sha256) günlüğe yaz,
çalışmayı kopya üzerinde yap. Kopyalar sunucu dışında, şifreli saklanır.

- [ ] API logları: `journalctl -u smartfinance-api.service --since "<başlangıç>" -o short-iso > api-log.txt`
- [ ] Caddy erişim logları (yapılandırmadaki log dosyası ve
      `journalctl -u caddy.service`). Bak: alışılmadık IP'ler, 401/403/429
      yoğunlukları, büyük yanıtlar, beklenmeyen uçlar.
- [ ] Uygulama log olayları: `journalctl -u smartfinance-api.service` çıktısında
      RateLimitReddi, GirisBasarisiz, YenilemeBasarisiz (aynı IP'den yoğunluk =
      kaba kuvvet denemesi) ve GirisBasarili (hangi hesaplara hangi IP'lerden
      girildi) satırlarını ara. Ayrıca `Beklenmeyen hata` kayıtlarına ve şifre
      sıfırlama loglarına bak.
- [ ] Veritabanı: olaydan önceki son yedeği ayrı tut (üzerine yazılmasın).
      Etkilenmiş olabilecek tablolar: Users, Transactions, Accounts, Transfers,
      RecurringPayments, Investments, Budgets, Categories, CategoryMappings,
      Notifications, ImportLogs, Subscriptions, DeviceTokens, RefreshTokens,
      PasswordResetTokens.
- [ ] Sunucu durumu: `last`, `/var/log/auth.log`, çalışan süreçler, son
      değişen dosyalar. Çıktıları dosyaya al.

## 4. Etki analizi

Günlüğe şu üç soruyu yanıtla:

- [ ] **Hangi veri türleri?** (kimlik ve iletişim, parola özeti, finansal
      işlemler, hesap ve kart bilgileri, portföy, cihaz token'ları, abonelik
      kayıtları, IP adresleri)
- [ ] **Kaç kullanıcı?** Kesin sayı yoksa üst sınırı ve nasıl hesaplandığını yaz.
- [ ] **Hangi tarih aralığı?** İhlalin başlangıcı, bitişi, fark edilme anı.
- [ ] Olası sonuçlar: kimlik avı, dolandırıcılık, parola tekrar kullanımı vb.

## 5. Bildirim (72 saat içinde)

- [ ] **Kişisel Verileri Koruma Kurulu'na bildir.** Kurul'un internet
      sitesindeki güncel **veri ihlali bildirim formu** kullanılır. Form zaman
      zaman değişiyor: doldurmadan önce güncel halini kontrol et.
      Bilgi eksikse eldekiyle bildir, kalanını sonra tamamla.
- [ ] 72 saat aşılırsa gecikmenin **gerekçesini** bildirimle birlikte yaz.
- [ ] **İlgili kişilere bildir**, makul en kısa sürede. Mevcut yollar:
      uygulama içi duyuru ve push (duyuru komutu). Metinde: ne oldu, hangi
      veriler, ne yaptık, kullanıcı ne yapmalı (ör. aynı parolayı başka yerde
      kullanıyorsa değiştirmesi), iletişim adresi.
- [ ] **Eksik:** toplu e-posta gönderme yolu henüz yok. Push izni olmayan ve
      uygulamayı açmayan kullanıcılara ulaşmak için bu yol gerekebilir.

## 6. Sonrası

- [ ] Kök neden: açık nasıl oluştu, neden fark edilmedi.
- [ ] Kalıcı düzeltme ve yeniden olmaması için önlem (kod, yapılandırma,
      izleme). Düzeltmenin canlıya çıktığını doğrula.
- [ ] **İhlal kayıt defterine** ekle (her olay için, bildirim yapılmasa bile):
      tarih, kapsam (veri türleri, kişi sayısı, tarih aralığı), nedeni, alınan
      önlemler, Kurul'a ve kişilere bildirim tarihleri.
- [ ] Bu kontrol listesinde eksik kalan adımları güncelle.
