# Planlı ödemeler — Aşama 1 teslim notu

19.10.2026 kullanıcı onayı. Yalnız backend. Önce temel takvim, servis ve hatırlatma
test dosyaları yazıldı, ardından uygulama ve ek sınır testleri eklendi.
Build/test/EF komutu çalıştırılmadı; mevcut 483 testin veya yeni testlerin geçtiği
iddia edilmiyor. Ağ, veritabanı, ortam değişkeni ve yapılandırma JSON'u okunmadı.
Migration yazılmadı, commit/push/dal değiştirme yapılmadı.

## Dosyalar

Yeni:

- `SmartFinance.Domain/Enums/RecurrenceFrequency.cs`
- `SmartFinance.Domain/Entities/RecurringPayment.cs`
- `SmartFinance.Application/DTOs/RecurringPayment/RecurringPaymentDtos.cs`
- `SmartFinance.Application/Interfaces/IRecurringPaymentService.cs`
- `SmartFinance.Application/Exceptions/ConflictException.cs`
- `SmartFinance.Infrastructure/Configurations/RecurringPaymentConfiguration.cs`
- `SmartFinance.Infrastructure/Recurring/RecurrenceSchedule.cs`
- `SmartFinance.Infrastructure/Recurring/RecurringReminderRunner.cs`
- `SmartFinance.Infrastructure/Recurring/RecurringReminderService.cs`
- `SmartFinance.Infrastructure/Services/RecurringPaymentService.cs`
- `SmartFinance.API/Controllers/RecurringPaymentController.cs`
- `SmartFinance.Tests/RecurrenceScheduleTests.cs`
- `SmartFinance.Tests/RecurringPaymentServiceTests.cs`
- `SmartFinance.Tests/RecurringReminderRunnerTests.cs`
- Bu teslim notu.

Değişen:

- `SmartFinance.Domain/Enums/NotificationType.cs`: RecurringDue = 3.
- `SmartFinance.Application/Common/FreeTierLimits.cs`: RecurringPayments = 3.
- `SmartFinance.Infrastructure/Context/SmartFinanceDbContext.cs`: RecurringPayments DbSet.
- `SmartFinance.Infrastructure/Services/AccountService.cs`: hareketsiz hesabın 204
  yolunda, silinmişler dahil bağlı planların AccountId alanı null yapılır.
  Mevcut 204 yolu soft-delete yapar; bu davranış korundu. Arşivleme planları değiştirmez.
- `SmartFinance.Infrastructure/Services/AuthService.cs`: DeleteAccountAsync içindeki
  kalıcı silme sırası Transactions → Transfers → RecurringPayments → Accounts →
  Categories → Users. Her aşamadaki mevcut SaveChanges ve dış transaction korundu.
  Budgets/CategoryMappings gibi mevcut bağımlıların temizliği ve Notifications'ın
  kullanıcı FK'sındaki Cascade yolu korunur.
- `SmartFinance.API/Program.cs`: yalnız scoped servis ve hosted service kayıtları.
- `SmartFinance.API/Middleware/ExceptionMiddleware.cs`: 409 eşlemesi ve 402'de
  premiumRequired=true. Görevin 6. maddesinin açık istisnasıyla değiştirildi.
- `SmartFinance.Tests/ExceptionMiddlewareTests.cs`: yalnız iki yeni test; eski
  testlerin beklentileri değiştirilmedi.

## Kesin JSON sözleşmesi

Bütün uçlar kimlik doğrulama ister (yoksa 401). Alanlar camelCase. Tutarlar JSON
sayıdır. `type`: 1 gelir, 2 gider. `frequency`: 1 haftalık, 2 aylık, 3 yıllık.
DateOnly alanlar `yyyy-MM-dd`; işlem tarihi UTC ISO 8601 (`Z`).

| Uç | İstek | Başarı |
| --- | --- | --- |
| GET /api/recurring | Gövde yok | 200 RecurringPaymentDto[] |
| GET /api/recurring/upcoming?days=30 | Gövde yok; days varsayılan 30, 1–90 | 200 UpcomingRecurringPaymentsDto |
| POST /api/recurring | CreateRecurringPaymentDto | 201 RecurringPaymentDto |
| PUT /api/recurring/{id} | CreateRecurringPaymentDto, tam güncelleme | 200 RecurringPaymentDto |
| DELETE /api/recurring/{id} | Gövde yok | 204, gövde yok |
| POST /api/recurring/{id}/pay | PayRecurringPaymentDto | 200 PayRecurringPaymentResultDto |
| POST /api/recurring/{id}/skip | ProcessRecurringPaymentDto | 200 RecurringPaymentDto |

POST /api/recurring örnek istek:

```json
{"name":"Deneme Dijital Abonelik","amount":17.25,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","endDate":null,"remindDaysBefore":1}
```

201 örnek yanıt (örneklerde TSİ bugün 19.10.2026):

```json
{"id":1,"name":"Deneme Dijital Abonelik","amount":17.25,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","anchorDay":19,"nextDueDate":"2026-10-19","endDate":null,"remindDaysBefore":1,"isActive":true,"daysUntil":0,"status":"today"}
```

GET /api/recurring örnek yanıt, NextDueDate sonra Id artan; pasifler de listelenir:

```json
[{"id":1,"name":"Deneme Dijital Abonelik","amount":17.25,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","anchorDay":19,"nextDueDate":"2026-10-19","endDate":null,"remindDaysBefore":1,"isActive":true,"daysUntil":0,"status":"today"}]
```

GET /api/recurring/upcoming?days=30 örnek yanıt:

```json
{"items":[{"recurringPaymentId":1,"name":"Deneme Dijital Abonelik","amount":17.25,"type":2,"categoryId":null,"accountId":null,"dueDate":"2026-10-19","status":"today"}],"expenseTotal":17.25,"incomeTotal":0}
```

Pencere bugün dahil bugün+days dahil. Aktif planların bütün vadeleri üretilir;
haftalık plan birden fazla satır olabilir. NextDueDate geçmişteyse bu gecikmiş
vade yalnız bir kez eklenir; planın pencereye düşen gelecek vadeleri de listelenir.
EndDate sonrası dahil edilmez. Sıralama dueDate, recurringPaymentId artan.
Toplamlar listedeki bütün satırları, gecikmişler dahil, toplar. Bu bir öngörüdür;
gelecek vadeyi ödemek için önce planın mevcut NextDueDate'i işlenmelidir.

PUT /api/recurring/1 örnek istek:

```json
{"name":"Deneme Dijital Abonelik","amount":21.75,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","endDate":null,"remindDaysBefore":1}
```

200 örnek yanıt:

```json
{"id":1,"name":"Deneme Dijital Abonelik","amount":21.75,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","anchorDay":19,"nextDueDate":"2026-10-19","endDate":null,"remindDaysBefore":1,"isActive":true,"daysUntil":0,"status":"today"}
```

POST /api/recurring/1/pay örnek istek (yalnız dueDate zorunlu):

```json
{"dueDate":"2026-10-19","amount":null,"transactionDate":"2026-10-19T12:00:00Z","accountId":null,"description":null}
```

200 örnek yanıt, mevcut TransactionDto alanları aynen korunur:

```json
{"transaction":{"id":11,"userId":1,"amount":21.75,"description":"Deneme Dijital Abonelik","merchantName":null,"transactionDate":"2026-10-19T12:00:00Z","type":2,"categoryId":null,"createdDate":"2026-10-19T12:00:00Z","accountId":null},"recurringPayment":{"id":1,"name":"Deneme Dijital Abonelik","amount":21.75,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","anchorDay":19,"nextDueDate":"2026-11-19","endDate":null,"remindDaysBefore":1,"isActive":true,"daysUntil":31,"status":"upcoming"}}
```

`amount` null/yoksa plan tutarı; `transactionDate` null/yoksa UTC şimdi;
`accountId` null/yoksa plan hesabı; `description` null/yoksa plan adı.
Bu geçici değerler planın tutarını/hesabını/adını değiştirmez. CategoryId ve Type
plandan alınır. Hesapsız ödeme için planın hesabı önce PUT ile null yapılabilir;
pay isteğinde null bir override değil, plan hesabını kullan anlamındadır.

POST /api/recurring/1/skip örnek istek (yukarıdaki ödemeden sonra):

```json
{"dueDate":"2026-11-19"}
```

200 örnek yanıt:

```json
{"id":1,"name":"Deneme Dijital Abonelik","amount":21.75,"type":2,"categoryId":null,"accountId":null,"frequency":2,"startDate":"2026-10-19","anchorDay":19,"nextDueDate":"2026-12-19","endDate":null,"remindDaysBefore":1,"isActive":true,"daysUntil":61,"status":"upcoming"}
```

DELETE /api/recurring/1: istek gövdesi yok, 204 yanıt gövdesi yok (JSON dönmez).
Plan soft-delete edilir; geçmiş işlemler değişmez. Sonraki erişim 404'tür.

## Alanlar ve kurallar

Create/PUT alanları: name, amount, type, categoryId, accountId, frequency,
startDate, endDate, remindDaysBefore. Name boş olamaz, en fazla 100 karakter;
trim edilerek saklanır. Amount pozitif, decimal(18,2) sınırında ve en fazla iki
ondalık basamaklı olmalı. Type/Frequency geçerli enum değerleri olmalı.
StartDate zorunlu; EndDate null veya StartDate'e eşit/sonra. RemindDaysBefore
0–7, gönderilmezse 1; 0 yalnız vade günü bildirimi demektir. Kategori kullanıcıya
ait ve aynı türde, hesap kullanıcıya ait ve arşivlenmemiş olmalı.

RecurringPaymentDto bunlara id, anchorDay, nextDueDate, isActive, daysUntil,
status ekler. AnchorDay sunucuda StartDate.Day'den türetilir. İstemci NextDueDate,
AnchorDay veya IsActive yazmaz. DaysUntil TSİ bugüne göre gün farkı; negatif
gecikmiş demektir. Durumlar:

- `overdue`: aktif ve NextDueDate < bugün.
- `today`: aktif ve NextDueDate == bugün.
- `upcoming`: aktif ve NextDueDate > bugün.
- `ended`: IsActive=false; tarih karşılaştırmasından önce uygulanır.

Oluşturmada geçmiş StartDate, bugün veya sonraki ilk vadeye ilerletilir.
Frequency/StartDate değişen PUT aynı hesabı tekrar yapar ve aktifliği yeni
takvime göre belirler. Diğer alanlar değişince vade korunur. EndDate vadenin
önüne alınırsa plan pasif olur; yalnız EndDate uzatmak bitmiş planı etkinleştirmez.
Takvim yeniden hesaplanırken ilk uygun vade EndDate sonrasındaysa plan pasiftir.

Haftalık +7 gün. Aylık kısa ayda ay sonu, sonraki ayda AnchorDay'e dönüş
(31 Ocak → 28/29 Şubat → 31 Mart). Yıllık aynı ay/AnchorDay, 29 Şubat artık
olmayan yılda 28 Şubat, artık yılda tekrar 29 Şubat. EndDate dahil son gün.
Pay/skip yalnız bir sonraki vadeye ilerler; aradaki gecikmiş vadeleri topluca
atlamaz. Sonraki vade EndDate'i aşarsa IsActive=false ve status=ended olur.

Ücretsiz sınır yalnız oluşturmada aktif, silinmemiş kayıt sayısına göre 3.
Premium sınırsız. Premium bittikten sonra mevcut planların listelenmesi,
güncellenmesi, ödenmesi, atlanması ve hatırlatmaları devam eder.

## Hatalar

Mevcut hata gövdesi: statusCode, message, traceId. 402 ayrıca premiumRequired=true.

- 400: geçersiz alan, eksik dueDate, pasif plan, kategori sahiplik/tür hatası,
  hesap sahiplik/arşiv hatası, UTC olmayan transactionDate veya geçersiz days.
  Pay description en fazla 500; override amount da plan tutarıyla aynı sınırlarda.
- 404: başkasına ait, bulunamayan veya silinmiş plan.
- 402: ücretsiz planda dördüncü aktif planı oluşturma.
- 409: dueDate mevcut NextDueDate ile eşleşmiyorsa veya eşzamanlı vade işleme
  yarışında diğer istek önce kaydettiyse. Son vade planı bitirse de eski dueDate
  tekrarı 409'dur; mevcut vadeyi pasif kayıtta işlemek 400'dür.

402 örnek:

```json
{"statusCode":402,"message":"Ücretsiz planda en fazla 3 planlı ödeme tanımlayabilirsiniz. Sınırsız planlı ödeme için Premium'a geçin.","traceId":"deneme-iz","premiumRequired":true}
```

409 örnek:

```json
{"statusCode":409,"message":"Bu vade zaten işlendi.","traceId":"deneme-iz"}
```

## Hatırlatma ve push

Her gün 09:00 Europe/Istanbul. Açılış TSİ 09:00 dahil–21:00 hariç arasındaysa
hemen bir tur; sonra ertesi 09:00. Hata loglanır, hosted service döngüsü sürer.
Windows/Linux saat dilimi çözümü mevcut KAP servisindeki kalıpla aynıdır.

Aktif/silinmemiş plan ve silinmemiş kullanıcı gerekir. NextDueDate baz alınır;
gecikmiş vadeler için tekrar bildirim üretilmez. Ön hatırlatma yalnız
RemindDaysBefore > 0 ve bugün == NextDueDate - RemindDaysBefore ise gönderilir.

| Durum | Gider başlığı | Gelir başlığı | Metin örneği |
| --- | --- | --- | --- |
| 1 gün önce | Yaklaşan ödeme | Yaklaşan gelir | Deneme planı: ₺1.234,50 — yarın (20.10) |
| 2–7 gün önce | Yaklaşan ödeme | Yaklaşan gelir | Deneme planı: ₺1.234,50 — 3 gün sonra (22.10) |
| Vade günü | Bugün ödenecek | Bugün beklenen gelir | Deneme planı: ₺1.234,50 |

Notification.Type = 3 (RecurringDue).
DedupeKey = `planli:{id}:{NextDueDate:yyyy-MM-dd}:{once|gun}`, en fazla 100
karakter. IgnoreQueryFilters ile silinmiş bildirimler de dedupe kapsamındadır.
Mevcut UNIQUE(UserId,DedupeKey), aynı anda çalışan sunuculara karşı da korunur.
Ön hatırlatma ile vade günü iki farklı anahtardır.

Yalnız yeni kaydedilen bildirim push olarak gönderilir. IPushSender yoksa
uygulama içi bildirim yeterlidir. Push data: type=notification, notificationId
(string). Hata loglanır, kaydedilmiş bildirim geri alınmaz. Geçersiz cihaz
token'ları silinir. Telefonu olmayan kullanıcı yine bildirim listesinde görür.

## Migration için beklenen model farkları

Migration DOSYASI YAZILMADI. Sunucuda kullanıcı üretecek.

- **RecurringPayments** tablosu: Id int PK; CreatedDate datetime2; UpdatedDate
  datetime2 nullable; IsDeleted bit; UserId int zorunlu; Name nvarchar(100)
  zorunlu; Amount decimal(18,2) zorunlu; Type int; CategoryId int nullable;
  AccountId int nullable; Frequency int; StartDate date; AnchorDay int;
  NextDueDate date; EndDate date nullable; RemindDaysBefore int default 1;
  IsActive bit. Nullable belirtilmeyen alanlar zorunludur.
- FK: UserId → Users.Id, CategoryId → Categories.Id, AccountId → Accounts.Id;
  hepsi Restrict. İndeks (UserId,NextDueDate); EF FK konvansiyonuyla CategoryId
  ve AccountId indeksleri.
- Query filter: !IsDeleted. NextDueDate, IsActive, IsDeleted EF concurrency
  token'larıdır; ek rowversion sütunu yoktur.
- Mevcut tablolarda sütun değişikliği YOK. NotificationType yalnız yeni enum
  değeri (3) alır; Notifications şeması/indeksleri değişmez.
- Doğrulama servis katmanında; ek SQL CHECK constraint yoktur.

## İşlem oluşturma ve doğrulama sınırı

PayAsync mevcut ITransactionService.CreateTransactionAsync yolunu kullanır.
Scoped DbContext üzerinde vade önce değiştirilir; TransactionService'in ilk
SaveChanges çağrısı işlem ve vade değişikliğini TEK atomik kayıtta yazar.
SQL Server'ın SaveChanges transaction'ı ve vade/aktiflik/silinme concurrency
kontrolü çift dokunmayı korur. Ardından mevcut bütçe değerlendirmesi ve push
yan etkileri aynen çalışır. TransactionDto'ya alan eklenmedi.

Mevcut işlem yolunda bütçe bildirimi kayıttan sonra ayrı SaveChanges ile
oluşur. Bu aşamada veritabanı hatası olursa ödeme/vade kaydedilmiş olabilir;
aynı dueDate ile tekrar 409 verir. Bu mevcut yan etki sırası değiştirilmedi.

Yeni testlerde her metodun NEDEN yorumu var; uydurma ad ve örnek tutarlar
kullanıldı. Üç yeni test sınıfı ve middleware'e eklenen iki test toplam 38 test
metodu, InlineData satırlarıyla 47 test vakası tanımlar. Mevcut test beklentisi
değiştirilmedi. Sözleşmedeki 12 JSON örneği ayrıştırıldı; yeni C# dosyalarının
süslü parantez dengesi ve `git diff --check` kontrol edildi. Statik inceleme yapıldı,
ancak build/test çalıştırılmadığı için derleme ve test sonucu doğrulanmış değildir.
InMemory, SQL Server FK sırasını ve SQL transaction geri almasını doğrulamaz;
migration sonrası gerçek sağlayıcıda bu iki nokta ayrıca doğrulanmalı.

ÖNERİLEN DERS: InMemory başarılı olsa da Restrict FK silme sırası ayrıca incelenmeli;
takvim günü TSİ üzerinden türetilmeli; vade tüketimi işlemle aynı SaveChanges'te olmalı.
