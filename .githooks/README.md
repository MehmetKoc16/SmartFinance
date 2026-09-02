# Git kancaları

`pre-commit` — commit'e hassas veri girmesini engeller (banka ekstresi,
imzalama anahtarı, API anahtarı, IBAN, yapılandırma dosyasındaki uzun sır
değerleri).

Bu depo **herkese açık**. Public bir depoda sır bir kez push'landığında yanmış
sayılır: geçmişten silmek dalı temizler ama GitHub erişilemez nesneleri bir
süre SHA ile servis etmeye devam eder ve depoyu klonlamış olan herkeste
kopyası kalır. Bu yüzden savunma commit'ten **önce** olmak zorunda.

## Kurulum

Kancalar `.git/hooks` içinde durmadığı için klonlandıktan sonra bir kez
etkinleştirilmeleri gerekiyor:

```sh
git config core.hooksPath .githooks
```

Yeni bir makinede veya yeni bir klonda **bu komut çalıştırılmazsa kanca
sessizce devre dışı kalır.** Kontrol etmek için:

```sh
git config --get core.hooksPath   # ".githooks" yazmalı
```

## Yanlış alarm

Kancanın haksız yere durdurduğundan eminsen:

```sh
git commit --no-verify
```

Bunu alışkanlık haline getirme — atlatmadan önce gerçekten ne commit'lediğine
bak. Kanca sık sık yanlış alarm veriyorsa doğru çözüm onu atlatmak değil,
desenini düzeltmek.
