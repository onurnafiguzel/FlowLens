# Faz 3 — özet

**Durum:** tamamlandı, kabul kriterleri karşılandı. **Tarih:** 2026-08-08.
Ayrıntı: `phase-3-notes.md` (tasarım ve bulgular) · `phase3-validation.md` (elle doğrulama) ·
`known-limitations.md` (açık maddeler).

## Ne üretildi

```
flowlens build <solution> -o graph.json     →  415 node · 966 kenar · 32 kök · 637 KB · ~32 s
flowlens trace "POST /api/ordering/checkout"           →  12 tablo, 62 kolon   ~1,5 s
flowlens trace "table:ordering.orders" --direction backward  →  4 endpoint + 1 background job
```

16 tablo · 97 kolon · 8 DbContext, hiçbiri veritabanına bağlanmadan (`EfProbe`, EF Core `IModel`).
Kökler: 25 endpoint · 3 consumer · 4 background service — hepsi node'unda `RootKind` taşıyor.
İkinci sınıf kenar oranı **%2,8** (27/966). 142 test, 0 atlanan.

## Ölçülen doğruluk

Dört endpoint forward + üç sorgu backward, ModularCommerce kaynağı ve `Migrations/*.cs` elle
okunarak. Gerçeklik kaynağı graph.json **değil**, hedef reponun kendisi.

| Ölçüm | Recall | Precision |
|---|---|---|
| **Tablo** | **%82** (9/11) | **%100** |
| **Kolon** | **%83** (29/35) | **%100** |
| **Backward — kök** | **%100** (9/9) | **%100** |

**Tek bir yanlış tablo, kolon veya kök yok.** Tüm sapma eksiklik yönünde.

### EF içi ve EF dışı ayrı raporlanmalı

Tek bir %82 aracın nerede güvenilir nerede kör olduğunu gizler:

| Kapsam | Tablo recall |
|---|---|
| **EF üzerinden çalışan akışlar** (cart, identity, cancel) | **%90** (9/10) |
| **EF dışı akış** (discovery — tüm veri erişimi ham SQL) | **%0** (0/1) |

**Aracın kapsamı EF'in kapsamıdır**, hedef reponun tamamı değil. EF'in gördüğünü görüyor; ham SQL'i
ve ilişkisel olmayan depoları görmüyor — ama **görmediği her yeri `file:line` ile raporluyor**
(4 ham SQL sitesi diagnostics'te). Kayıp gerçek, sessiz değil.

Alt kırılımlar: INSERT yollarında kolon recall **%84**, UPDATE yollarında **%100**.
Ambiguous politikasının ("tüm implementasyonlar") veri katmanı maliyeti **0 tablo, 0 kolon**;
`ExternalCall` katmanında **1/1 yanlış pozitif** (varsayılan konfigürasyonda hiç koşmayan
`HttpEmbeddingService`).

## Açık kalan altı madde

| # | Ne | Sınıf | Kayıt |
|---|---|---|---|
| **F2** | Redis / ilişkisel olmayan depolar ontolojide yok | **Ontoloji kararı** — kod değil, kapsam sorusu | L17 |
| **F4** | Owned navigasyon okuması READS üretmiyor (`order.Lines`) | Düzeltilebilir | L19 |
| **F5** | Interceptor kuralı tablo düzeyinde; outbox'ın 4 kolonu yok | Düzeltilebilir, **kararı verildi** (§5.9 → B) | L16-4 |
| **F6** | Ham SQL tabloları görünmüyor (`product_embeddings` R) | **YAPISAL** — SQL parse roadmap'te yasak | L6 |
| **F9** | Kolon backward'ı yalnız "kim yazıyor"u cevaplar | Bilinçli sınır | L18-2 |
| **F10** | Backward'daki "Data layer" bloğu hedefin kendi kolonları | Kozmetik | L18-3 |

Kapatılanlar: F1 (jsonb kapsayıcı kolon) · F3 (satır düzeyi INSERT kuralı) · F7 (F6'nın yan etkisi,
`mechanism` ile ayırt edilebilir) · F8 (`RootKind`).

## Faz 4'ün cevaba yansıtması gerekenler

Faz 4 = `POST /ask` + LLM. **Doğruluk Faz 3'te üretildi; Faz 4 onu bozmamakla yükümlü.**
`GET /trace`, yukarıdaki `flowlens trace "<node>"` komutunun karşılığıdır — `graph.json` üzerinde,
solution yüklemeden.

**Cevaba mutlaka girmesi gerekenler:**

1. **Diagnostics, cevabın parçası.** F6 ve F2 yüzünden "bu akış hiçbir tabloya dokunmuyor" cümlesi
   **yasak**. `POST /api/discovery/search` için doğru cevap *"tablo bulunamadı"* değil,
   *"bu akış ham SQL kullanıyor (`ProductVectorRepository.cs:60`), tablo çıkarılamadı"*. LLM'e
   giden bağlamda ilgili diagnostic satırları da olmalı.
2. **`mechanism` alanı ayrımı taşımalı.** İkinci sınıf iddialar (`EntityConstruction`,
   `SaveChangesWithEntityParameter`) ve satır düzeyi iddialar (`RowInsert`) aynı güvenle
   sunulmamalı. Ölçülmüş örnek: `payment.payments.RefundedAtUtc` checkout'ta gerçekten yazılıyor
   (INSERT kolonu listeliyor) ama bu bir *niyet* değil — `RowInsert` etiketi bunu ayırt ediyor.
3. **Kök tipi cevapta görünmeli.** `RootKind` sayesinde backward *"4 endpoint + 1 arka plan işi"*
   diyebiliyor. "4 endpoint" demek eksik cevaptır ve Faz 5a triage bot'unda şüpheli kaybettirir.
4. **F9 için boş küme ≠ cevap.** *"Bu kolonu kim okuyor?"* sorusunun graph'ta karşılığı yok. LLM
   boş liste döndürmemeli, **soruyu cevaplayamadığını söylemeli** — sessiz boş küme bu projede
   bulunan her ciddi hatanın biçimi.
5. **F5 kolon düzeyinde eksik.** `ordering.outbox_messages` tablo düzeyinde doğru, kolon düzeyinde
   boş. Cevap tabloyu saymalı, o tablo için kolon iddiasında bulunmamalı.
6. **F4 yüzünden R/W ayrımı temkinli olmalı.** Owned navigasyon okumaları görünmediği için bir tablo
   "yalnız yazılıyor" gibi görünebilir. *"Bu tablo okunmuyor"* iddiası kurulmamalı.
7. **F10 nedeniyle backward çıktısı olduğu gibi LLM'e verilmemeli.** O bloktaki kolonlar hedefin
   kendi kolonları, ulaşan akışların yazdıkları değil.

**Hazır iki kol:** `TraversalQuery.IncludeUtility: false` (bağlamı küçültür) ve `mechanism`
(iddia sınıfını taşır).

**Faz 4'e girmeyecek olan:** `graph.json` invariant kontrolünün build'e bağlanması ve eval set
tasarımı — ikisi de Faz 5, girdileri `phase-3-notes.md` §10'da hazır.
