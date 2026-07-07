# SmartFinance — Proje Durum Belgesi
> Son güncelleme: Haziran 2026
> Bu dosya her yeni ajan oturumunda projeyi hızlıca anlamak için kullanılır.

---

## Proje Yapısı

### Backend — SmartFinance/ (ASP.NET Core 9, C#)
```
SmartFinance.Domain/          -> Entity, BaseEntity, Enum
SmartFinance.Application/     -> DTO, Interface, Exceptions
SmartFinance.Infrastructure/  -> DbContext, EF Config, Services, BankParsers
SmartFinance.API/             -> Controllers, Program.cs (DI)
SmartFinance.Tests/           -> (Henuz bos)
```

### Frontend — SmartFinance-Mobile/ (Flutter)
```
lib/
  core/constants/app_colors.dart   -> Renkler ve gradientler
  screens/                         -> Tum ekranlar
  services/api_service.dart        -> HTTP istekleri
  widgets/                         -> Paylasilan widget'lar
```

---

## Veritabani — SQL Server (SmartFinanceDb)

| Tablo | Aciklama |
|-------|----------|
| Users | Kullanicilar (FullName, Email, PasswordHash) |
| Categories | Kategoriler — kullaniciya ozel (UserId FK) |
| Transactions | Gelir/gider (CategoryId nullable int?) |
| Investments | Yatirimlar (hisse, altin, doviz, kripto) |
| CategoryMappings | PDF import icin ogrenme tablosu |

NOT: Tum tablolarda soft delete (IsDeleted = true) var.
NOT: Transaction.CategoryId nullable — PDF'den kategorisiz gelen islemler icin.
NOT: Yeni kayit olurken 8 varsayilan kategori otomatik olusur: Maas, Yeme-Icme, Ulasim, Fatura, ATM, Transfer, Alisveris, Diger

---

## Tamamlanan Backend Endpoint'leri

POST   /api/auth/register
POST   /api/auth/login

GET    /api/transaction/filter     -> Filtreli liste (page, pageSize, type, categoryId, startDate, endDate)
GET    /api/transaction
GET    /api/transaction/{id}
POST   /api/transaction
PUT    /api/transaction/{id}
DELETE /api/transaction/{id}       -> Soft delete
GET    /api/transaction/summary/{year}/{month}

GET    /api/category
POST   /api/category
PUT    /api/category/{id}
DELETE /api/category/{id}

GET    /api/investment
POST   /api/investment
PUT    /api/investment/{id}
DELETE /api/investment/{id}
GET    /api/investment/summary

POST   /api/pdfimport/parse        -> PDF yukle, islemleri parse et (multipart, field: "file")
POST   /api/pdfimport/confirm      -> Parse edilenleri DB'ye kaydet

---

## Flutter Ekran Durumu

| Ekran | Dosya | Durum |
|-------|-------|-------|
| Giris | login_screen.dart | TAM CALISIYOR |
| Kayit | register_screen.dart | TAM CALISIYOR |
| Dashboard | dashboard_screen.dart | TAM — gelir/gider/bakiye, donut grafik, ay degistirme |
| Islemler | transactions_screen.dart | KISMI — liste/sayfalama/filtre VAR, duzenleme/silme YOK |
| Islem Ekle | add_transaction_screen.dart | TAM CALISIYOR |
| Kategoriler | categories_screen.dart | TAM CALISIYOR |
| Yatirimlar | investments_screen.dart | TAM CALISIYOR |
| PDF Import | pdf_import_screen.dart | TAM — 3 adimli akis (Yukle/Onizle/Onayla) |
| Profil | profile_screen.dart | KISMI — cikis calisiyor, isim hardcoded "SmartFinance", butonlar bos |

---

## Eksik / Yapilmamis

### Kritik
1. Islem duzenleme ve silme — backend PUT/DELETE var ama Flutter cagirmiyor
2. GET /api/auth/me endpoint'i YOK — kullanici bilgisi cekilemiyor
3. Profil'de "Profil Duzenle" ve "Sifre Degistir" butonlari tamamen bos (() {})
4. Sifre degistirme — backend'de de endpoint yok

### Orta Oncelik
5. Islemler ekraninda kategori ve tarih araligina gore filtre
6. Islem arama (aciklamaya gore)
7. Harcama analizi grafikleri (kategori bazli dagilim, aylik trend)

### Uzun Vadeli
8. Yatirim fiyatlari otomatik cekme (BIST, altin, doviz API)
9. Azure Document Intelligence (resim tabanlı PDF'ler)
10. Push bildirimler

---

## Teknik Detaylar

### API Base URL (Flutter)
http://10.0.2.2:5059/api
(10.0.2.2 = Android emulatorunden localhost)

### Backend Port
http://localhost:5059

### JWT
SharedPreferences'da "auth_token" key'i ile saklanir.

### Flutter API Cagrilari
```dart
final result = await ApiService.authenticatedGet('/endpoint');
final result = await ApiService.authenticatedPost('/endpoint', {body});
final result = await ApiService.authenticatedUpload('/endpoint', filePath, fileName);
```

### Backend UserId Alma Pattern'i
```csharp
var userId = int.Parse(_httpContextAccessor.HttpContext!.User
    .FindFirst(ClaimTypes.NameIdentifier)!.Value);
```

### Renkler (AppColors)
purple  = 0xFF8B5CF6
cyan    = 0xFF06B6D4
green   = 0xFF10B981
red     = 0xFFEF4444
orange  = 0xFFF59E0B
cardBg  = 0xFF1E1B2E
cardBgLight = 0xFF252238

### PDF Import Teknik
- PdfPig kutuphanesi kullaniliyor
- GetWords() + Y-koordinat gruplama (tablo destegi)
- Parser'lar: HalkbankParser, ZiraatParser, GenericBankParser (fallback)
- 5+ haneli sayilar description'dan temizleniyor
- Duplicate kontrol: UserId + Tarih + Tutar kombinasyonu

### file_picker Notu (v11.0.2)
FilePicker.pickFiles() kullanilir — FilePicker.platform.pickFiles() DEGIL

### Migrations (sirayla)
1. InitialCreate
2. AddCategoryMapping
3. AddInvestment
4. MakeCategoryIdNullable

### Yeni Modul Ekleme Sirasi
1. Domain/Entities/ -> Entity
2. Application/DTOs/ -> DTO'lar
3. Application/Interfaces/ -> Interface
4. Infrastructure/Configurations/ -> EF config (HasQueryFilter(!IsDeleted))
5. Infrastructure/Services/ -> Service
6. API/Controllers/ -> Controller ([Authorize])
7. DbContext'e DbSet<T> ekle
8. Program.cs'e DI kaydi ekle
9. Migration calistir

