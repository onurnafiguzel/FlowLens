# FlowLens — Genel Bakış

Bu doküman projenin tamamını tek yerde anlatır: ne yaptık, neden yaptık, nasıl çalışıyor, sınırları ne.

---

## 1. Tek cümlede

FlowLens, bir .NET solution'ını Roslyn ve EF Core metadata'sıyla okuyup **hangi endpoint'in hangi tablolara ve kolonlara dokunduğunu** deterministik olarak çıkaran, sonucu HTTP API, dokümantasyon sitesi ve incident raporu olarak sunan bir static analysis aracıdır.

Analiz ettiği hedef: **ModularCommerce** (senin .NET 10 modular monolith portföy projen).

---

## 2. Çözdüğü üç problem

| # | Problem | Bugün nasıl çözülüyor | FlowLens ile |
|---|---|---|---|
| 1 | **İş analisti impact analizi soruyor** — "ödeme akışına yeni provider ekleyeceğiz, hangi tablo/kolon etkilenir?" | Bir developer'ın 20-30 dakikası, cevap kişiye göre değişiyor | `GET /trace?node=...` → 2 ms, `file:line` referanslı |
| 2 | **Incident triage** — "iade butonu 500 dönüyor, nereye bakacağım?" | Loglara bakılıyor, local'de debug ediliyor | `flowlens triage --stack-trace` → etkilenen akışlar + tablolar + son commit'ler |
| 3 | **Onboarding** — "sipariş akışı nereden nereye gidiyor?" | Birine sormak veya günlerce kod okumak; doküman varsa eskimiş | `out/` altında 37 dosya, GitHub'da render olan diyagramlar |

Üçünün ortak zemini aynı: **kod tabanındaki akışın makine tarafından okunabilir bir haritası yok.**

---

## 3. Mimari — tek çekirdek, dört tüketici

```
ModularCommerce (kaynak kod)
        │
        │  flowlens build          ~25-32 sn · günde bir kez veya CI'da
        ▼
   graph.json                      ← TEK DOĞRULUK KAYNAĞI
   415 node · 966 kenar            (bellekte ~3,3 MB)
        │
        ├──► HTTP API        /trace /backward /endpoints /tables /graph/stats   ~2 ms
        ├──► Dokümantasyon   flowlens docs -o out/   → 37 dosya
        ├──► Triage          flowlens triage --stack-trace
        └──► Eval            flowlens eval           → ölçüm raporu
```

### En kritik mimari karar

**Doğruluk deterministik katmanda üretilir. LLM, varsa, sadece arayüzdür.**

| Katman | Deterministik mi |
|---|---|
| Extraction (Roslyn + EF Core `IModel`) | **Evet** — ground truth |
| Storage (`graph.json`) | Evet |
| Traversal (C# BFS) | Evet |
| API / Dokümantasyon / Triage / Eval | Evet |
| Doğal dil arayüzü (Faz 8, opsiyonel) | Hayır — ve **izole** |

Sebep: impact analizinde %95 doğruluk işe yaramaz. Yanlış kolon = eksik migration = production hatası.

---

## 4. Üç proje — görevleri ve referans yönü

```
┌─────────────────┐     ┌─────────────────┐     ┌──────────────────┐
│  FlowLens.Cli   │     │  FlowLens.Api   │     │ FlowLens.Tests   │
│  (console)      │     │  (Minimal API)  │     │  (xunit, 294)    │
└────────┬────────┘     └────────┬────────┘     └────────┬─────────┘
         │                       │                       │
         └───────────────────────┴───────────────────────┘
                                 │
                                 ▼
                        ┌─────────────────┐
                        │  FlowLens.Core  │
                        │  (class library)│
                        └─────────────────┘
```

**Bağımlılık tek yönlü.** `Core` hiçbirine referans vermez. `Cli`, `Api`, `Tests` yalnız `Core`'a bakar. Aralarında hiç referans yok — CLI, API'yi tanımaz; API, CLI'yı tanımaz.

Faz 8 yazılırsa `FlowLens.Llm` dördüncü proje olacak ve **`Core` ona da referans vermeyecek** — kapalıyken her şey çalışmaya devam edecek.

### FlowLens.Core — çekirdek

Tüm mantık burada. Katmanlar:

| Klasör | Sorumluluk |
|---|---|
| `SolutionLoader`, `CallGraphWalker` | Roslyn: solution yükleme, call chain, sembol çözümleme |
| `EfProbe`, `EfModelSnapshot` | EF Core `IModel`: entity → tablo, property → kolon |
| `GraphBuilder`, `GraphModel`, `GraphJson` | Node/edge üretimi, kanonik serileştirme |
| `CodeGraph`, `NodeResolver` | Forward/backward BFS, node id çözümleme |
| `GraphSource`, `GraphPathResolver` | `graph.json` yükleme, tazelik, arama sırası |
| `Answers/AnswerBuilder` | **Dar bel** — `Subgraph` → `TraceAnswer` projeksiyonu |
| `Docs/` | Mermaid + markdown üretimi |
| `Triage/` | Stack trace parse, çerçeve eşleştirme, git log okuma |
| `Evals/` | Eval runner, skorlama, rapor |

`AnswerBuilder` özellikle önemli: **beş tüketici de oradan geçiyor** (CLI, API, docs, triage, eval). Onu ölçmek beşini birden ölçer — Faz 7'nin eval set'i bu yüzden ona koşuyor.

**Paketler:** `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.Build.Locator`, EF Core + Npgsql (hedefinkine eşit sürüm).

### FlowLens.Cli — komut satırı

```
flowlens build <solution> [-o graph.json]     graph üretir       ~25-32 sn
flowlens trace "<node>" [--direction backward] graph'ı gezer      ~1,5 sn
flowlens docs -o out/                          37 dosya üretir    ~1 sn
flowlens triage --stack-trace <dosya>          incident raporu
flowlens eval [-o report.md]                   ölçüm raporu
```

`build` dışındaki hiçbir komut solution yüklemez — hepsi `graph.json` okur.

**Kritik teknik detay:** `Program.cs` yalnız iki satır — `MSBuildLocator.RegisterDefaults()` ve `Runner.RunAsync(args)`. MSBuild tiplerine dokunan kod ayrı sınıfta, `[MethodImpl(NoInlining)]` ile. Sebep JIT: bir metoda girildiğinde gövdesindeki **tüm tipler** resolve edilir, dolayısıyla `RegisterDefaults()` "ilk satırda" olsa bile geç kalır.

### FlowLens.Api — HTTP yüzeyi

Minimal API, beş endpoint, istek süresi **0,4–1,9 ms**:

```
GET /endpoints          25 endpoint: route, method, modül, konum
GET /trace?node=...     forward — tablolar, kolonlar, R/W, event köprüleri
GET /backward?node=...  backward — RootKind'a göre gruplu kökler
GET /tables             16 tablo, şema ve modül ile
GET /graph/stats        node/edge sayıları, diagnostics, üretim zamanı, okunan dosya yolu
```

**Solution asla yüklenmez** — bu bir kabul kriteri ve testle sabitli. API sadece `graph.json` okur, her istekte `mtime + dosya uzunluğu` kontrol eder, değişmişse yeniden yükler. Parse hatası **son iyi snapshot'ı düşürmez**.

Graph yoksa: veri uçları **503** + `application/problem+json` (denenen tüm yollar + çözüm komutu), ama `/graph/stats` **200** döner ve `status: "unavailable"` der — *"neden bozuk" sorusu her şey 503'ken cevaplanabilmeli.*

**Tip seviyesinde bir garanti:** `dataLayer` backward cevabında `null`, `entryPoints` forward cevabında yok. Yanlış okuma konvansiyonla değil **tiple** engelleniyor.

---

## 5. Faz faz — ne yaptık, ne bulduk

### Faz 0 — Kurulum ve keşif
Proje iskeleti + ModularCommerce'in yapısının çıkarılması (`modularcommerce-survey.md`). Solution yapısı, endpoint tanımlama şekli, DbContext konumları, MassTransit kullanımı.

### Faz 1 — Roslyn'e ısınma
**Görev:** solution'ı güvenilir şekilde yükleyip metotları saymak. Grafik yok, AI yok.

**Sonuç:** 66/66 proje, 4 bağımsız hata sinyali (`WorkspaceFailed`, proje sayısı karşılaştırması, `SupportsCompilation`, opsiyonel `GetDiagnostics`).

**Öğrenilen — MSBL001:** `Microsoft.Build.Framework`'ün iki kopyası, `MSBuildLocator`'ın resolver'ını devre dışı bırakıyor. Locator'ın engellemek için var olduğu hata, kendi paket zincirinden geliyor.

**Tool'un ilk faydası:** survey "68 proje" demişti, doğrusu **66**.

### Faz 2 — Call chain
**Görev:** bir endpoint'ten başlayıp çağrı zincirini recursive çıkarmak.

**Bloke edici:** 25 endpoint'in tamamı Minimal API **lambda**'sı — `MethodDeclarationSyntax` bunları görmüyor. Faz 1'de yakalandı, Faz 3'e sarkmadı.

**Öğrenilen — sembol kimliği compilation başına, solution başına değil.** `OrderPaid` iki compilation'da farklı `ITypeSymbol`; `SymbolEqualityComparer` eşit görmüyor. Sonuç: modüller arası tek köprü (`Ordering → Notification`) **sessizce hiç kurulmuyordu.** Projeler arası eşleme yapan her sözlük tam nitelikli isme çevrildi.

**Ambiguous politikası:** tüm implementasyonlar eklenir, `ambiguous: true` işaretlenir. Sonradan ölçüldü: dekoratör ve koleksiyon enjeksiyonunda "tümü" doğru cevap; yalnız config anahtarıyla seçilenlerde aşırı-yaklaşım.

### Faz 3 — Graph + tablo/kolon
**Görev:** EF Core `IModel`'i bağlayıp `graph.json` üretmek. **Projenin değeri burada görünür oldu.**

**Sonuç:** 8 DbContext, 16 tablo, 97 kolon. Checkout akışı **12 tablo, 62 kolon**.

| Ölçüm | Sonuç |
|---|---|
| Tablo recall (EF içi) | %90 |
| Tablo recall (EF dışı / raw SQL) | **%0** |
| Kolon recall | %83 |
| Precision | %100 *(sonradan L21 ile düzeltildi)* |

**Öğrenilen:** 110 test yeşilken graph **üç yerde sessiz yanlış cevap** veriyordu — `kind` alanı varsayılanlarda serialize edilmiyor, `Column → Table` kenarı yok, outbox erişilemiyor, orphan endpoint'ler. Testlerin bulamadığını `graph.json`'ı **elle okumak** buldu. Faz 7'nin varlık sebebi bu.

**Kayıp sessiz değil:** raw SQL noktaları diagnostics'te `file:line` ile duruyor. Graph *"dokunmuyor"* demiyor, *"bakamadım"* diyor.

### Faz 4 — Deterministik API
**Görev:** analistin `dotnet run` çalıştırmak zorunda kalmaması.

**Tek satır üretim kodu yazılmadan üç hata bulundu** — hepsi bir varsayılanı seçmek için yapılan ölçümden:

1. **`Walk` erişilebilirlik filtresi uyguluyordu**, sunum filtresi değil. Bir utility node'un arkasında kalan *utility olmayan* her şey sessizce düşüyordu; 16 backward sorgusunun 4'ünde bir kök kayboluyordu.
2. **`RootKind` dolu bir node utility etiketlenebiliyordu.** İnvariant: bir kök, tanımı gereği yardımcı olamaz.
3. **`graph.json` çıktı sırası deterministik değildi** — tek alanlık değişiklik 216 satırlık diff üretiyordu, yani dosya **elle okunamaz** hale geliyordu. Faz 3'ün dört bulgusunu bulan şey tam olarak dosyayı elle okumaktı.

**Ölçüm tuzağı:** ilk `/trace` ölçümü 138 ms gösterdi. PowerShell'in `Invoke-WebRequest`'i 109 KB gövdeyi nesnelere çeviriyordu; `HttpClient` ile **1,91 ms**. Ölçüm aracı, ölçülenin 70 katı gürültü üretmişti.

### Faz 5 — Dokümantasyon & görselleştirme
**Görev:** onboarding. `flowlens docs -o out/` → 37 dosya.

**Beş karar ölçümle değişti:**

| Karar | Ölçüm | Sonuç |
|---|---|---|
| Subgraph ile modül kutulama | 33 kenarın **12'si ilgisiz kutuyu kesiyor** | Reddedildi, modül etikete taşındı |
| Sabit yön (LR) | 896 px'i aşan **20/25** | En geniş fan ≤ 7 → `TD` → **6/25** |
| Yön eşiği değişkeni | 12 node'lu iki diyagram zıt cevap veriyor | Node sayısı değil, **en geniş fan** |
| "Sığmıyor" notu | Jeneratörün renderer'ı yok, piksel ölçülemez | Koşulsuz, iddiasız satır + mermaid.live bağlantısı |
| Kardeş kenar sırası (`1..n`) | 36 grubun 13'ü aynı çağrı yerini paylaşıyor | Numara adım değil, **çağrı yeri** sırası |

**Kaynak sırası:** alfabetik sıralama vakaların **%61'inde** kaynak sırasından farklıydı — okuyucu soldan sağa okuyup kod sırası sanıyordu. `CALLS` kenarlarına `callSite` (dosya, satır, kolon) eklendi. Numaralı adımların **%19'u koşullu** (ternary, if/else, switch, catch) ve işaretleniyor.

**Determinizm — Faz 4'ten farklı bir ders:** Faz 4'ünki çıktı sıralamasıydı. Faz 5'inki bir katman derinde — **sıralamayı deterministik yapmak yetmez, keşfin kendisi deterministik olmalı.** `seen` kümesi bir düğüme hangi kenarın bağlanacağını keşif sırasına bırakıyordu; çıktıyı sıralamak bunu **gizlerdi**, düzeltmezdi.

### Faz 6 — Triage Bot
**Görev:** stack trace → graph. Yeni doğruluk kaynağı kurulmadı, Faz 4'ün `AnswerBuilder`'ı ters yönde çağrıldı.

**Üç hüküm — "graph'ta yok" ile "çağrı yok" aynı şey değil:**

| Hüküm | Anlamı |
|---|---|
| **eşleşti** | node bulundu, id + `file:line` |
| **graph'ta yok** | proje namespace'i ama node yok — *"FlowLens bu çerçeveyi göremedi"* |
| **proje dışı** | framework / 3. parti |

`src/` altındaki 300 dosyanın **147'sinin** hiç node'u yok — yani üçüncü hüküm ana vaka.

**Uygulama öncesi üç ölçüm (Adım 0):**

- **0a — async çerçeve biçimi.** Hatırlananın yarısı yanlış çıktı: async metotlar demangle ediliyor, async **lambda** edilmiyor. Parametreler CLR kısa adıyla geliyor (`Int32`), node id C# adıyla (`int`) — takma ad tablosu gerekti.
- **0b — inlining çerçeve DÜŞÜRÜYOR.** Release'de üç çerçeveden ikisi silindi; `[MethodImpl(NoInlining)]` taşıyan kontrol ayakta kaldı. ModularCommerce'e etkisi: **255 metot düğümünün 97'si (%38) risk altında.**
- **0c — fixture merdiveni.** "Gerçek stack trace" iddiası dört basamaklı bir düşüş sırasıyla karşılandı. Sonuç: **4 gerçek, 0 sentetik.**

**Sınır çağrılabilir yüzeyle uygulandı:** `GitLog` yalnız `rev-parse` ve `log` çıkarabiliyor. *"Git'e yazmıyoruz"* bir yorum değil, **API'nin özelliği**.

### Faz 7 — Eval set
**Görev:** cevabın doğru **ve tam** olduğunu ölçmek.

**Neden gerekliydi:** 275 test yeşildi ama **hiçbiri recall ölçmüyordu.** Domain testleri `Assert.Contains` ile yazılmış — bir **çapa**, listede olmayanı asla göremez.

**Sonuç:** 22 soru, hiçbir eksende %100 recall yok.

| Eksen | Recall | Precision |
|---|---:|---:|
| tablo (EF içi) | %97,1 | %100 |
| tablo (EF dışı) | %75,0 | %100 |
| kolon-yazma (EF içi) | %81,6 | %96,4 |
| **kolon-okuma** | **%0,0** | — |
| kök | %76,5 | %100 |
| event | %60,0 | %100 |
| **dış depo** | **%0,0** | %0 |

**Asıl bulgu tool'da değil, eval set'in kendisinde çıktı:**

- **Q01 ↔ Q19 çelişkisi** — aynı köprü hakkında iki soru iki farklı şey iddia ediyordu. Eval set kendi iç tutarlılığını sınadı ve tutarsız çıktı.
- **Eksen kapısı iki hatayı koşudan değil kapıdan buldu** — `expectedToFail` girdisinin gerçekleşebileceği bir eksen `expected`'da yoktu. Öngörü yanlış değildi, **ölçülemiyordu**.
- **Dört yeni sınır:** L21 (identity kolonları), L22 (publish atfı), L23 (iç içe owned tip), L24 (raw-sql geri yön boşluğu).

**L21 özellikle önemli:** Faz 3'ün precision'ı %100 kayıtlıydı. Rakam düşmedi — **yanlış soruyla ölçülmüştü**: *"migration'da kolon var mı"* sorulmuş, doğrusu *"bu akış onu yazıyor mu"* idi.

**Eval set'in ölçülen hata payı:** 3 düzeltme + 13 doğrulama. Sıfır değil, **ölçülmüş ve kapatılmış.**

---

## 6. Docker Faz 7'de (ve Faz 6'da) nerede kullanıldı

Docker **üretim bağımlılığı değil** — yalnız *ölçüm* için kullanıldı, iki yerde:

### Faz 6 — gerçek stack trace yakalamak (0c)

Fixture'ların "gerçek" olduğu iddiası bir merdivenle karşılandı. Gerçek **Postgres 17** ve **pgvector** container'ları ayağa kaldırıldı, harness ModularCommerce'in **derlenmiş DLL'lerini** referans alıp gerçek hatalar üretti:

| Fixture | Hata |
|---|---|
| A | `42P01: relation "inventory.stock_audit" does not exist` (bozuk audit trigger) |
| A2 | `42703: column s.UpdatedAtUtc does not exist` (eksik migration) |
| B | `42P01: relation "discovery.product_embeddings" does not exist` |

**ModularCommerce'e tek bayt yazılmadı.**

### Faz 7 — EF'in gerçek SQL'ini yakalamak (`sqlprobe`)

Kolon kuralı (7. adım) başta FlowLens'in kendi kuralından türetilmişti — **döngüsel**: kuralın implementasyonundaki bir hata hem oracle'da hem tool'da olur, eval bunu göremez.

Bağımsız kaynak gerekti. Gerçek Postgres container'ı + EF SQL logging ile dört vaka koşuldu:

```
A) INSERT  Order.Create + Orders.Add + SaveChanges
B) UPDATE  reload + MarkPaymentPending + MarkPaid
C) INSERT  StockItem.Create + Add
D) UPDATE  StockItem.Reserve
```

**Bulgu:** EF, `IdentityByDefaultColumn` kolonlarını INSERT'e **yazmıyor** — `RETURNING id` ile geri okuyor. FlowLens üçünü de iddia ediyordu. Aynı gerekçe (`değeri veritabanı üretir`) bir ailede (`xmin`) hariç tutmaya, diğerinde (`id`) dahil etmeye götürüyordu.

Kuralın gerekçesi böylece *"FlowLens böyle diyor"*dan *"EF böyle yazıyor"*a döndü. **L21** bu ölçümden doğdu.

---

## 7. Yeni endpoint eklendiğinde ne olur?

**Otomatik değil.** Zincir şöyle:

```
1. ModularCommerce'te kod değişir
2. flowlens build <solution>     ← ELLE, ~25-32 sn
   → graph.json güncellenir
3. API otomatik fark eder        ← mtime + uzunluk kontrolü, her istekte
4. flowlens docs -o out/         ← ELLE, ~1 sn
   → 37 dosya yeniden üretilir
5. git commit out/               ← ELLE
```

| Ne | Tazelenme |
|---|---|
| `graph.json` | `flowlens build` koşunca |
| **API** | **Otomatik** — `graph.json` değişince, her istekte kontrol |
| Dokümantasyon (`out/`) | `flowlens docs` koşunca |
| Triage / Eval | Koşuldukları anda mevcut `graph.json`'ı okur |

**Neden tam otomatik değil:** 66 projeyi Roslyn ile derlemek 25-32 saniye sürüyor — bu bir HTTP cevabı olamaz. `graph.json` tam olarak bu yüzden var: pahalı hesabı bir kez yapıp ucuz sorguları binlerce kez koşmak.

**Otomatikleştirme yolu (roadmap'te "faz sonrası"):** CI'da her PR'da `build` + `docs` koşup `out/`'u güncelleyen bir workflow. Bunun ek bir faydası var — `git diff`'te `dependencies.md`'nin değişmesi *"bu PR yeni bir modül bağımlılığı ekliyor"* sinyali verir. O sinyal ancak dosya commit'liyse doğar.

**Bayat graph riski kapatıldı:** `flowlens build` hedef assembly'nin kaynak koddan eski olduğunu tespit edip **stale build uyarısı** basıyor, ve `/graph/stats` hangi dosyayı okuduğunu (`graphFilePath`) ve ne zaman üretildiğini (`graphFileWrittenAt`) gösteriyor.

---

## 8. Ekibe yeni katılan ne yapacak?

**Hiçbir şey kurmadan, GitHub'da `out/` klasörünü açacak.**

```
out/
├── README.md                          index — 37 dosyaya bağlantı
├── flows/                             25 endpoint, her biri bir akış diyagramı
│   ├── post-api-ordering-checkout.md
│   ├── delete-api-cart-items-productid-guid.md
│   └── ...
└── modules/                           10 modül dokümanı + bağımlılık grafiği
    ├── dependencies.md                ← modüller arası harita
    ├── Ordering.md
    └── ...
```

**Bir akış sayfasında ne var:**

- **Mermaid diyagramı** — GitHub'da doğrudan render oluyor. Endpoint → Handler → Repository → Table, event köprüleri ayrı stille. Her node'da modül etiketi (`Cart · cart.carts`).
- **Numaralı adım listesi** — kaynak sırasında, her adımda `dosya:satır`. Koşullu adımlar işaretli.
- **Veri katmanı** — tablolar, R/W ayrımı, kolonlar.
- **Bilinen sınırlar** — *"bu akış şurada ham SQL kullanıyor, o tablo listede yok"*.
- **mermaid.live bağlantısı** — diyagram dar geliyorsa büyütmek için.

**Bir modül dokümanında ne var:** hangi endpoint'ler, hangi tablolar (R/W), hangi event'ler publish/consume ediliyor, hangi modüllere bağımlı, ve o modülün bilinen sınırları.

**`modules/dependencies.md`** en değerli tek dosya: 8 node / 9 kenar, üç kenar stili — sözleşme çağrısı (meşru), event (meşru), **Contracts dışından doğrudan referans** (ihlal adayı, kalın/mor).

> Araç kuralı uygular, **hüküm vermez**: diyagram "ihlal" demez, "Contracts dışından doğrudan referans" der ve `file:line` verir.

**Yerel olarak da açılabilir:** VS Code'da `Markdown Preview Mermaid Support` eklentisiyle `Ctrl+Shift+V`.

---

## 9. Başlangıç hedefleri vs bugün

| Başlangıçtaki fikir | Bugün |
|---|---|
| "Analist keyword girip akışı görsün, chatbot .md dosyalarını tarasın" | ✅ Çözüldü — ama **daha iyi**: LLM'in ürettiği .md'leri taramak yerine deterministik graph. Analist `GET /trace` ya da `out/` dosyalarını kullanıyor |
| "Alert maili dinleyen bot branch açsın, fix yazsın" | ⚠️ **Bilinçli olarak yapılmadı**. Triage raporu var, otomatik fix yok. Gerekçe `design-decisions.md` D1–D4: alert storm'da geri besleme döngüsü, log'lardaki PII, review edilmemiş yamanın sahte güveni |
| "Graphify/Spec Kit gibi araçlarla endpoint akışı görselleştirilsin" | ✅ Çözüldü — kendi yazdığın üreteçle, 25 diyagram, deterministik |

### Roadmap'in üç problemi

| Problem | Durum |
|---|---|
| 1 — Analist impact analizi | ✅ API + dokümantasyon, ölçülmüş doğrulukla |
| 2 — Incident triage | ✅ `flowlens triage`, 4 gerçek fixture ile test edilmiş |
| 3 — Onboarding | ✅ 37 dosyalık living documentation |

### Sayılarla

| | |
|---|---|
| Faz | 7 tamamlandı (+1 opsiyonel) |
| Test | **294**, 0 atlanan |
| Graph | 415 node · 966 kenar · 16 tablo · 97 kolon |
| Dokümantasyon | 37 dosya, byte-identical deterministik |
| Eval | 22 soru, 9 eksende ölçülmüş recall/precision |
| Bilinen sınır | L1–L24, her biri gerekçeli |
| API isteği | 0,4–1,9 ms |

---

## 10. Tool'un ModularCommerce'te bulduğu şeyler

Bunlar FlowLens'in kendi işini yaptığının somut kanıtı:

1. **Survey "68 proje" diyordu, doğrusu 66.**
2. **`ModularCommerce.Host` bayat build'di** — assembly kaynak koddan eskiydi, tablo/kolon adları eşleşmeyebilirdi.
3. **`OrderCancelled` publish ediliyor ama hiçbir consumer dinlemiyor.**
4. **`Shared`'daki bir background service `catalog.products`'a doğrudan yazıyor** (seeder — gözlem, ihlal değil).
5. **Kod kendini yanlış anlatıyor:** `Order.cs:150-151` *"W10 tüketicileri (Shipping/Notification) iptali dinler"* diyor; `src/` altında öyle bir consumer **yok**.

Beşincisinin genel dersi: **yorum bir kanıt değil.** `notes.evidence` bildirime bakar, açıklamaya değil.

---

## 11. Kalıcı sınırlar — dürüstçe

FlowLens **genel amaçlı bir ürün değil.** ModularCommerce'in konvansiyonlarına bağlı çalışır ve şunları göremez:

| Sınır | Ne demek |
|---|---|
| **Ham SQL** (L6) | `dataSource.CreateCommand`, `ExecuteSqlAsync` ile erişilen tablolar görünmez. Discovery modülünün tablo recall'ı **%0**. Ama kayıp sessiz değil — 4 site diagnostics'te `file:line` ile |
| **İlişkisel olmayan depolar** (L17) | Redis'e yazan bir akış node üretmez. `ExternalCall` yalnız `HttpClient` çağrılarını tanıyor |
| **Kolon okuması** (L18-2) | Kolon node'ları yalnız bir **yazma** onları adlandırdığında üretiliyor. *"Bu kolonu kim okuyor?"* sorusunun graph'ta karşılığı yok |
| **Owned navigasyon okuması** (L19) | EF auto-include ettiği için SQL'de tablo var ama sözdiziminde `DbSet` yok — bir tablo "yalnız yazılıyor" görünebilir |
| **Inlining** (L20) | Release'de JIT çerçeve düşürüyor; 255 metot düğümünün %38'i risk altında. Statik analiz göremez |
| **İç içe owned tip** (L23) | `OwnsMany` içindeki `OwnsOne`'ın kolonları node olarak **hiç üretilmiyor**. EF zorunluluğu, ModularCommerce'e özgü değil |
| **Raw-SQL geri yön** (L24) | *"Bu tabloya bakamadığım bir yer var"* uyarısı **geri sorularda çıkmıyor** — tam da en çok gerektiği soruda |
| **Config-seçimli DI** (L3/L11) | `IReservationStrategy` ve `IEmbeddingService` config anahtarıyla seçiliyor; FlowLens hepsini listeliyor. Aşırı-yaklaşım, kayıtlı politika |
| **Reflection / dynamic** | Hedef repo kullanmıyor → **popülasyon 0**, ölçülemedi. Kullanan bir repoda çalışmaz |

**Neden generic yapılmadı:** her ORM ve her endpoint stilini desteklemek, öğrenilmek istenen şeyi öğrenmeden karmaşıklık eklemek olurdu. Bu bilinçli bir kapsam kararı.

---

## 12. Tekrar eden metodolojik dersler

Bu proje boyunca aynı ders üç seviyede öğrenildi:

| Faz | Ders | Sorulacak soru |
|---|---|---|
| 5 | Test **yanlış satırı** koruyordu | *"Testim gerçekten neyi koruyor?"* |
| 6 | Test doğruydu, **popülasyon** sessizdi | *"Testimi tetikleyecek girdi elimde var mı?"* |
| 7 | Öngörü yanlış değildi, **ölçülemiyordu** | *"Bu iddiayı gerçekleştirecek eksen var mı?"* |

Bunlara eşlik eden kurallar:

- **Sessiz kayıp yasak.** Elenen, çözülemeyen veya atlanan her şey `file:line` ile diagnostics'e yazılır. Graph *"dokunmuyor"* ile *"bakamadım"*ı ayırt etmelidir.
- **Popülasyon kuralı.** Fixture bir **örneklem**, graph **popülasyonun kendisi**. Eksik testi fixture'dan değil graph'tan seç.
- **Ölçmeden konuşma.** Faz 4'te iki kez, Faz 6'da bir kez "düzeltildi" denildi ve ölçüm aksini gösterdi.
- **Ölçüm aracı da ölçülür.** `Invoke-WebRequest` ölçülenin 70 katı gürültü üretmişti; eval set'in kendi hata payı 3 düzeltmeyle ölçüldü.
- **Doğru cevap ≠ doğru sebep.** Bir tablonun graph'a *tesadüfen* doğru girmesi mümkün; kenarlar bu yüzden `mechanism` + `evidence` taşıyor.

---

## 13. Faz 8 — opsiyonel, izole

Doğal dil arayüzü (`POST /ask`). **Doğruluğa hiçbir şey katmaz**; iki şey katar: analistin endpoint adını bilmek zorunda olmaması, ve projenin tezinin *gösterilebilir* hale gelmesi.

**İzolasyon kuralı:** `FlowLens.Llm` ayrı proje, `FlowLens.Core` ona referans vermez, yapılandırmayla kapatılabilir, kapalıyken her şey çalışır. Kodun tamamı LLM'e gönderilmez — yalnız kullanıcının sorusu ve C# tarafından hazırlanmış dar bir node listesi.

**Parite tasarımı hazır:** her eval sorusu `question` (doğal dil) ve `selector` (oracle çözüm) alanlarını ayrı taşıyor. Faz 8'de iki hata kaynağı ayrışacak — **hedefleme** (LLM yanlış node seçti) ve **aktarım** (LLM cevabı bozdu).

**Yapılmazsa da proje tamamdır.** Kurumsal gerçek: büyük şirketler kaynak kodunu harici bir LLM sağlayıcısına göndermek istemiyor. LLM'e bağımlı bir tool bu kurumlarda değerlendirilmeye alınmaz; LLM'siz çalışan bir tool doğrudan kurulabilir.

---

## 14. Mülakatta anlatım — üç cümle

> *"Bir .NET modular monolith'in akış haritasını Roslyn ve EF Core metadata'sıyla çıkaran bir tool yazdım. Doğruluğu deterministik katmanda ürettim, LLM'i hiç kullanmadım — çünkü impact analizinde %95 doğruluk yanlış migration demek."*

> *"22 soruluk bir eval set ile ölçtüm: tablo recall'ı EF içinde %97, ham SQL kullanılan yerde %0. Tek bir ortalama vermiyorum çünkü aracın nerede kör olduğunu gizler."*

> *"En değerli bulgu tool'da değil, ölçüm setinin kendisinde çıktı: iki soru aynı köprü hakkında çelişen şeyler iddia ediyordu. Eval set'in kendi hata payını da ölçtüm — üç düzeltme."*

Buna eşlik eden somut artefaktlar: GitHub'da render olan 25 akış diyagramı, `dependencies.md`'de görünen mimari harita, ve `design-decisions.md`'de *"neden otomatik fix yazmıyor"* sorusunun gerekçeli cevabı.