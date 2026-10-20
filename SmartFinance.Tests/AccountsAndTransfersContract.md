# Çoklu hesap / transfer / kart ekstresi — Aşama 1 teslim notu

20.10.2026 (v16 yayın öncesi düzeltmeler): "Zaman kuralı" bölümü, PUT /transaction
`clearAccount`, arşivli hesapta aynı hesapla düzenleme ve tutar sınırları eklendi.

19.10.2026 kullanıcı sözleşmesi. Yalnız backend. Önce test dosyaları yazıldı.
Derleme, test ve EF komutu çalıştırılmadı; mevcut 444 testin veya yeni testlerin
geçtiği iddia edilmiyor. Ağ, ortam değişkeni, yapılandırma JSON'u ve migration
dosyası kullanılmadı. Commit/push yapılmadı.

## Dosyalar

Yeni:

- `SmartFinance.Domain/Enums/AccountType.cs`
- `SmartFinance.Domain/Entities/Account.cs`, `Transfer.cs`
- `SmartFinance.Infrastructure/Configurations/AccountConfiguration.cs`, `TransferConfiguration.cs`
- `SmartFinance.Infrastructure/Accounts/CardCycle.cs`
- `SmartFinance.Infrastructure/Services/AccountService.cs`
- `SmartFinance.Application/Interfaces/IAccountService.cs`
- `SmartFinance.Application/DTOs/Account/AccountDtos.cs`
- `SmartFinance.Application/DTOs/Transaction/UpdateTransactionDto.cs`
- `SmartFinance.API/Controllers/AccountController.cs`
- `SmartFinance.Tests/CardCycleTests.cs`, `AccountServiceTests.cs`, bu teslim notu

Değişen:

- `SmartFinance.Domain/Entities/Transaction.cs`: yalnız nullable AccountId ve navigation.
- `SmartFinance.Infrastructure/Configurations/TransactionConfiguration.cs`: Restrict hesap FK'sı.
- `SmartFinance.Infrastructure/Context/SmartFinanceDbContext.cs`: Accounts ve Transfers DbSet.
- `SmartFinance.Infrastructure/Services/TransactionService.cs`: hesap doğrulama, filtre ve DTO eşlemeleri.
- `SmartFinance.Infrastructure/Services/AuthService.cs`: yalnız DeleteAccountAsync; mevcut transaction ve sıra yorumları korunarak işlemlerden sonra transferler, sonra hesaplar kalıcı silinir.
- `SmartFinance.Application/DTOs/Transaction/CreateTransactionDto.cs`, `TransactionDto.cs`, `TransactionFilterDto.cs`: accountId. DeletedTransactionDto bunu mevcut kalıtımıyla alır.
- `SmartFinance.Application/Common/FreeTierLimits.cs`: Accounts = 2.
- `SmartFinance.API/Program.cs`: yalnız IAccountService DI kaydı.

TransactionController 19.10'da değişmedi. 20.10.2026 (v16): PUT ucu ve
`ITransactionService.UpdateTransactionAsync` artık `UpdateTransactionDto` alır
(CreateTransactionDto + `clearAccount`); POST gövdesi değişmedi.

## Kesin JSON sözleşmesi

Alanlar camelCase, türler JSON sayı (`1` nakit, `2` banka, `3` kredi kartı),
tutarlar JSON sayı. Bütün uçlar kimlik doğrulama ister. Hesap/transfer uçlarında
başkasına ait veya silinmiş kayıt 404; işlem oluşturma/güncellemede geçersiz hesap
400 ve `Hesap bulunamadı` mesajıdır. Hesap filtresinde başkasının hesabı 404.
Mevcut hata middleware/validation yanıt biçimi korunur.

| Uç | Sonuç |
| --- | --- |
| GET /api/account | 200, AccountDto[]; arşivler hariç |
| GET /api/account?includeArchived=true | 200, AccountDto[]; aktifler önce, SortOrder, ad, Id |
| POST /api/account | 201, AccountDto |
| PUT /api/account/{id} | 200, AccountDto; tür değişemez |
| DELETE /api/account/{id} | Hareket varsa 200 `{"archived":true}`; yoksa soft-delete ve gövdesiz 204 |
| POST /api/account/{id}/unarchive | 200, AccountDto; gövde yok |
| POST /api/account/transfer | 201, TransferDto |
| GET /api/account/{id}/transfers?page=1&pageSize=50 | 200, `{"items":[TransferDto],"totalCount":0}`; pageSize 1–100, tarih ve Id azalan |
| DELETE /api/account/transfer/{id} | Soft-delete, gövdesiz 204 |
| GET /api/account/{id}/statements?count=3 | 200, `{"current":StatementDto,"past":[StatementDto]}`; count 1–12, past yeniden eskiye |

POST ve PUT hesap gövdesi aynı alanlara sahiptir:

```json
{"name":"Deneme kartı","type":3,"openingBalance":100,"creditLimit":5000,"statementDay":20,"dueDay":25}
```

`name` zorunlu, boş olamaz, en fazla 60; `type` 1/2/3. `openingBalance`
opsiyonel veya null, varsayılan 0; kartta açılış borcu negatif olamaz.
Kart için pozitif `creditLimit` ve 1–28 arası `statementDay`, `dueDay` zorunlu.
Nakit/bankada bu üç alan gönderilmez veya null olur; doluysa 400.
Ücretsiz planda arşivlenmemiş hesap sınırı 2, premium sınırsız. Oluşturma ve
arşivden çıkarma için 402 mesajı:
`Ücretsiz planda en fazla 2 hesap tanımlayabilirsiniz. Sınırsız hesap için Premium'a geçin.`

AccountDto'nun bütün alanları, örnek kart yanıtı:

```json
{"id":1,"name":"Deneme kartı","type":3,"openingBalance":100,"creditLimit":5000,"statementDay":20,"dueDay":25,"isArchived":false,"sortOrder":0,"balance":null,"debt":100,"availableLimit":4900}
```

Nakit/banka için `balance` sayı; `debt`, `availableLimit`, kart alanları null.
Kartta `balance` null; borç ve kullanılabilir limit negatif olabilir.

Transfer oluşturma gövdesi:

```json
{"fromAccountId":2,"toAccountId":1,"amount":1000,"transferDate":"2026-10-19T12:00:00Z","description":"Deneme ödeme"}
```

TransferDto'nun bütün alanları:

```json
{"id":10,"fromAccountId":2,"toAccountId":1,"amount":1000,"transferDate":"2026-10-19T12:00:00Z","description":"Deneme ödeme"}
```

`description` opsiyonel/null, en fazla 200. Tarih zorunlu, UTC saklanır ve UTC
döner; istemci ISO 8601 `Z` göndermeli. Aynı hesap, pozitif olmayan tutar veya
arşivlenmiş hesap 400. Tutar en fazla iki ondalık basamaklı ve decimal(18,2)
sınırında olmalı; aksi 400 `Transfer tutarı en fazla iki ondalık basamaklı ve
decimal(18,2) sınırında olmalıdır.` Transfer gelir/gider satırı oluşturmaz.

`openingBalance` ve `creditLimit` da en fazla iki ondalık basamaklı ve decimal(18,2)
sınırında olmalı (400). Nakit/banka açılış bakiyesi negatif olabilir; kart açılış borcu
negatif olamaz, kart limiti pozitif olmalı.

StatementDto'nun bütün alanları:

```json
{"periodStart":"2026-09-21","periodEnd":"2026-10-20","dueDate":"2026-10-25","amount":1000,"paid":400,"remaining":600,"status":"partial"}
```

Tarihler `yyyy-MM-dd`. `status`: empty/paid/partial/unpaid/overdue; current her zaman
open. Kesimi geçmiş dönemde yalnız `amount == 0` ve `paid == 0` ise `empty` döner;
bu durumda `remaining` da 0'dır. `amount == 0` ama `paid > 0` durumunda mevcut
durum kuralları korunur. Kesim günü dahil, önceki kesimin ertesi günü dahil.
Gün kuralı için aşağıdaki "Zaman kuralı" bölümüne bakın (işlem günü dönüştürülmez,
transfer günü UTC→TSİ). Açılış borcu önce, bütün eski dönemler sonra FIFO
kapanır. `count` (1–12) kaç dönem geriye bakılacağını belirler; FIFO tarihçesini sınırlamaz.
`past` içinde `periodEnd < hesabın CreatedDate değerinin TSİ günü` ve `amount == 0`
ve `paid == 0` koşullarının tümünü sağlayan dönemler çıkarılır. Açılış günüyle aynı
günde kesilen boş dönem ve açılıştan önce tarihli harcama içeren dönem görünür kalır.
Çıkarılan dönemlerin yerine daha eski dönemler getirilmez; `past` kısalabilir veya
boş olabilir. Açılıştan sonra kesilen boş dönemler `empty` olarak listede kalır.
Ödeme fazlası sonraki/açık döneme taşınır.
Ekstre tutarı gider eksi iadedir; avans yalnız toplam kart borcunu etkiler.
Gelecek TSİ günündeki hareketler bugünün ekstresine/ödemesine katılmaz.

İşlem POST/PUT mevcut gövdesine yalnız opsiyonel `accountId` eklenir:

```json
{"amount":1000,"description":"Deneme harcama","transactionDate":"2026-10-19T12:00:00","type":2,"categoryId":7,"accountId":1}
```

Mevcut kategori doğrulaması değişmez. POST'ta `accountId` gönderilmez/null ise genel işlem.

PUT /api/transaction/{id} (20.10.2026, v16) ayrıca opsiyonel `clearAccount` (bool) alır.
v14/v15 istemcileri PUT gövdesinde `accountId` göndermez; bu yüzden:

| Gövde | Sonuç |
| --- | --- |
| `accountId` dolu | Doğrulanıp atanır (`clearAccount` yok sayılır). |
| `accountId` yok/null ve `clearAccount: true` | Hesap bağı kaldırılır. |
| `accountId` yok/null ve `clearAccount` yok/false | Mevcut bağ KORUNUR. |

```json
{"amount":1000,"description":"Deneme harcama","transactionDate":"2026-10-19T12:00:00","type":2,"categoryId":7,"accountId":null,"clearAccount":true}
```

Gövdedeki `accountId` işlemin mevcut hesabıyla aynıysa hesap arşivli olsa da güncelleme
kabul edilir (eski işlemin tutarı düzeltilebilir). Farklı bir arşivli/başkasının/silinmiş
hesaba taşımak 400 `Hesap bulunamadı`.
İşlem yanıtlarının alanları:
`id, userId, amount, description, merchantName, transactionDate, type, categoryId, createdDate, accountId`.
Çöp kutusu yanıtında bunlara `deletedAt` eklenir. Liste, detay, filtre, oluşturma
ve geri alma yanıtlarında accountId korunur. Güncelleme halen 204.
`GET /api/transaction/filter?accountId=1` mevcut filtre/sayfalama sözleşmesini korur:
`items, totalCount, page, pageSize, totalPages`.
Aylık özet, kategori özeti ve bütçe sorguları değişmez.

## Zaman kuralı (20.10.2026)

- `Transaction.transactionDate` = TSİ duvar saati, Kind belirsiz (`Z`/ofset YOK). Mobil ve
  PDF içe aktarma bugüne kadar hep böyle gönderdi; canlı kayıtlar bu biçimde. Sunucu
  UTC'ye de TSİ'ye de çevirmez; gün = tarihin kendi günü.
- `Transfer.transferDate` = UTC (`Z`). Gün = UTC→Europe/Istanbul dönüşümü.
- "Bugün"/"şimdi" = TSİ.
- Ekstre dönemi ve "gelecek TSİ günündeki hareket bugünün ekstresine/ödemesine
  katılmaz" kuralı bu günlerle uygulanır. Örnek: kesim 20, 20.10 22:00 duvar saatli
  harcama 20.10 ekstresindedir; 20.10 21:30Z transfer TSİ 21.10'dur.
- Hesap bakiye/borç (`balance`, `debt`, `availableLimit`) tarih süzmez; bütün hareketleri
  toplar (değişmedi).

## Migration için model farkları

Migration dosyası yazılmadı; sunucuda kullanıcının belirttiği
`dotnet ef migrations add AddAccountsAndTransfers` adımı gerekir.

- **Accounts**: Id (PK), CreatedDate, UpdatedDate nullable, IsDeleted (BaseEntity);
  UserId int; Name nvarchar(60) zorunlu; Type int; OpeningBalance decimal(18,2)
  zorunlu/default 0; CreditLimit decimal(18,2) nullable; StatementDay ve DueDay int
  nullable; IsArchived bit; SortOrder int.
- **Transfers**: BaseEntity sütunları ve Id PK; UserId, FromAccountId, ToAccountId
  int zorunlu; Amount decimal(18,2); TransferDate datetime2; Description nvarchar(200)
  nullable.
- **Transactions**: AccountId int nullable; mevcut satırlara backfill yapılmaz,
  null kalırlar.
- İndeksler: Accounts.UserId; Transfers.UserId; EF FK konvansiyonu ile
  Transfers.FromAccountId, Transfers.ToAccountId ve Transactions.AccountId.
- FK'lar: Accounts.UserId → Users.Id; Transfers.UserId → Users.Id;
  Transfers.FromAccountId ve ToAccountId → Accounts.Id;
  Transactions.AccountId → Accounts.Id. Hepsi Restrict.
- Account ve Transfer için `!IsDeleted` global query filtresi. Pozitif tutar,
  kart alanları ve aynı kullanıcı doğrulamaları servis katmanındadır; SQL CHECK
  constraint eklenmedi.

## Test adları

30 test metodu, InlineData satırlarıyla 39 test vakası tanımlı; her metodun üstünde
NEDEN yorumu var. Çalıştırılmadılar.

CardCycleTests:

- KesimTarihi_KisaAydaAyinSonGunu
- Donem_OncekiKesiminErtesiGunuDahil_KesimGunuDahil
- SonOdeme_DueDayKesimdenBuyukseAyniAy
- SonOdeme_DueDayKucukseErtesiAy
- TsiGunSiniri_UtcGeceYarisiSonrasiTransferDogruGune (20.10.2026: işlem yerine transfer üzerinden)
- Ekstre_KesimGunu22de_GirilenIslem_AyniEkstrede (20.10.2026)
- GelecekGunluIslem_DuvarSaatiGunuyleAyiklanir (20.10.2026)
- OdemelerFifo_EnEskiEkstreyiOnceKapatir
- FazlaOdeme_SonrakiEkstreyeGecer
- Gecikti_SonOdemeGectiVeKalanVar
- EkstreDurumu_OdenenTutaraGoreBelirlenir
- Fifo_GosterilenDonemSayisindanBagimsizdir
- Iade_NetHarcamayiAzaltir_GelecekOdemeSayilmaz

AccountServiceTests:

- NakitBakiye_AcilisGelirGiderTransfer
- KartBorcu_HarcamaIadeOdemeAvans
- KartOdemesi_AylikGidereCiftSayilmaz
- BaskasininHesabi_404
- UcretsizPlan_UcuncuHesap402VeyaMevcutPremiumHatasi
- Premium_SinirsizHesap
- IsleminiVarsaSilmekArsivler_YoksaSiler
- TurDegistirilemez400
- KartAlanlari_KartDisindaGelirse400
- Transfer_AyniHesap400_SifirTutar400_ArsivliHesap400
- IslemFiltresi_AccountIdIleSuzer
- ArsivdenCikarma_UcretsizSiniriKontrolEder
- Islem_GecersizHesap400_OlusturmaVeGuncelleme
- Islem_HesapBilgisiGuncellemeCopKutusuVeGeriAlmadaKorunur
- Transfer_SoftDeleteBakiyeyiGeriAlir_SayfalamaTariheGore
- Hesap_GecersizKartAlanlari400
- Ekstre_KartOlmayanHesapVeGecersizSayida400
- AcilisBakiyesi_VerilmezseSifir
- HesapSilme_KullaniciSilinceHesapVeTransferlerDeSilinir
- 20.10.2026 (v16): IslemGuncelle_AccountIdYoksa_HesapBagiKorunur,
  IslemGuncelle_ClearAccountTrue_BagKaldirilir, IslemGuncelle_YeniAccountId_Atanir,
  IslemGuncelle_ArsivliHesaptakiIslem_AyniHesaplaDuzenlenebilir, Transfer_UcOndalik_400,
  Transfer_AsiriBuyuk_400, Hesap_TutarAlanlari_SinirKontrolu

Elle iki kez kontrol edilen tutarlar:

- Nakit: 100 + 250 − 40 + 30 − 20 = **320**.
- Kart: 100 + 6000 − 200 − 500 + 50 = **5450**; limit 5000 − 5450 = **−450**.
- Kart harcaması 1000 + ödeme transferi 1000 → aylık gider **1000**, işlem adedi **1**.
- FIFO: ödeme 120 − açılış 50 = 70; eski 100'lük ekstre kalan **30**, yeniye **0** ödeme.
- Fazla ödeme: 400 − 50 − 100 − 200 = **50** açık döneme; açık harcama 80, kalan **30**.

## Derleme ve doğrulama sınırı

Using'ler, servis imzaları, DbSet adları, DI, DTO eşlemeleri ve değişiklik kapsamı
statik incelendi. `git diff --check` boşluk hatası bildirmedi. .NET 10 build/test/ef
çalıştırılmadı. Derlenebilirlik ve 444+39 testin sonucu sunucuda doğrulanmalı.
InMemory testleri SQL Server FK davranışını ve LINQ→SQL çevirisini doğrulamaz;
migration üretimi ve SQL Server sorguları sunucuda kontrol gerektirir.

ÖNERİLEN DERS: Ekstre görünümünün count sınırı FIFO hesap tarihçesini sınırlamamalı.
