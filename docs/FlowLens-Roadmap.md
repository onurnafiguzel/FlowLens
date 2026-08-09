# FlowLens — Code Flow & Impact Analysis Tool

> Bu doküman Claude Code'a context olarak verilmek üzere yazıldı.
> Kod yazmadan önce **"Claude Code için çalışma kuralları"** bölümünü oku.
> Faz prompt'ları: `docs/FlowLens-ClaudeCode-Prompts.md`
>
> **v2 — faz sırası değişti.** LLM katmanı en sona alındı ve izole edildi.
> Gerekçe Bölüm 4'te.

---

## 1. Problem

Büyük ekiplerde üç manuel süreç var:

1. **İş analisti impact analizi soruyor.** "Ödeme akışına yeni bir provider ekleyeceğiz, hangi tablo/kolon etkilenir?" → bugün bir developer'ın 30 dakikasını alıyor, cevap kişiye göre değişiyor ve eksik kalabiliyor.
2. **Incident triage.** "İade butonu 500 dönüyor" → loglara bakılıyor, local'de debug ediliyor, hangi katmanın bozulduğu elle bulunuyor.
3. **Onboarding.** Ekibe yeni katılan biri "sipariş akışı nereden nereye gidiyor" sorusunun cevabını ancak birine sorarak veya günlerce kod okuyarak buluyor. Dokümantasyon varsa da eskimiş.

Üçünün ortak zemini aynı: **kod tabanındaki akışın makine tarafından okunabilir bir haritası yok.**

## 2. Çözüm

Tek bir çekirdek, dört tüketici:

```
C# solution
    │
    ▼
[Roslyn static analysis]  ──►  nodes + edges
[EF Core IModel]          ──►  entity→table, property→column
    │
    ▼
graph.json  (tek dosya, source of truth)
    │
    ├──► HTTP API        : /trace, /backward, /endpoints         (Faz 4)
    ├──► Dokümantasyon   : Mermaid diyagram + modül dokümanları   (Faz 5)
    ├──► Triage          : stack trace → akış + son commit'ler    (Faz 6)
    └──► Doğal dil       : "iade akışı neye dokunuyor?"           (Faz 8, opsiyonel)
```

## 3. En kritik mimari karar

**Doğruluk deterministik katmanda üretilir. LLM, varsa, sadece arayüzdür.**

| Katman | Sorumluluk | Deterministik mi |
|---|---|---|
| Extraction | Roslyn + EF Core metadata | **Evet** — ground truth |
| Storage | `graph.json` | Evet |
| Traversal | C# BFS/DFS | Evet |
| API | HTTP, graph.json üzerinde | Evet |
| Dokümantasyon | Mermaid + markdown üretimi | Evet |
| Triage | stack trace → backward → git log | Evet |
| Doğal dil arayüzü | soru → parametre, sonuç → cümle | Hayır (LLM) |

**Neden bu ayrım:** impact analizinde %95 doğruluk işe yaramaz. Yanlış kolon = eksik migration = production hatası. LLM'e "kodu oku ve söyle" dedirtmek non-deterministik ve doğrulanamaz bir sistem üretir.

## 4. LLM neden en sonda ve neden izole

Üç sebep, üçü de bağımsız olarak yeterli:

**Kurumsal gerçek.** Büyük şirketler kaynak kodunu — özellikle çekirdek iş mantığını — harici bir LLM sağlayıcısına göndermek istemiyor. LLM'e bağımlı bir tool bu kurumlarda hiç değerlendirilmeye alınmaz. LLM'siz çalışan bir tool ise doğrudan kurulabilir.

**Öğrenme.** Roslyn, EF Core metadata ve graph modelleme bu projenin öğrenilecek asıl kısmı. LLM katmanı bunların üstünde ince bir kabuk; önce gelirse dikkati dağıtır ve zeminin eksiklerini gizler.

**Ürün değeri.** Faz 4 sonunda analistin sorusu cevaplanıyor, Faz 5 sonunda onboarding dokümantasyonu üretiliyor. İkisi de LLM olmadan. Doğal dil arayüzü konforu artırır, doğruluğa hiçbir şey katmaz.

### İzolasyon kuralı

`FlowLens.Llm` **ayrı bir proje** olacak ve:

- `FlowLens.Core` ona referans **vermeyecek** — bağımlılık yönü tek yönlü
- Yapılandırmayla kapatılabilecek; kapalıyken diğer her şey çalışacak
- Kapalıyken build'de LLM SDK'sı yer almayacak
- Kodun tamamı LLM'e gönderilmeyecek — yalnız kullanıcının sorusu ve C# tarafından hazırlanmış dar bir node listesi

Bu, mülakatta anlatılacak kararların en güçlülerinden biri: *"LLM'i çıkarınca ürünün çalışmaya devam etmesi tesadüf değil, mimari bir gereklilikti."*

## 5. Kapsam Dışı (bilinçli olarak yapılmayacak)

- ❌ Graph database (Neo4j, Gremlin) — `List<Node>` + LINQ yeterli
- ❌ Vector DB / embedding / RAG — graph traversal semantic search'ten daha doğru
- ❌ Taint analysis, data flow analysis, points-to analysis
- ❌ Otomatik branch açma / otomatik fix / auto-merge
- ❌ Multi-repo desteği
- ❌ Genel amaçlı olma iddiası — ModularCommerce'in konvansiyonlarına bağlı

**Faz sonrası (çekirdek bittikten sonra değerlendirilir):** MCP server, incremental cache, CI entegrasyonu, web arayüzü, OpenTelemetry karşılaştırması.

## 6. Teknik kısıtlar

- **Hedef repo:** ModularCommerce (.NET 10, modular monolith, DDD, MassTransit + RabbitMQ Outbox/Inbox, EF Core, PostgreSQL)
- **FlowLens ayrı bir repodur.** ModularCommerce'in kaynak kodunu okur, ona hiçbir şey eklemez.
- **Ana paketler:** `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.Build.Locator`, EF Core + Npgsql (hedefinkine eşit sürüm)
- **Dil:** kod ve yorumlar İngilizce, dokümantasyon Türkçe

## 7. Veri modeli

### Node tipleri

| Tip | Örnek | Kaynak |
|---|---|---|
| `Endpoint` | `POST /api/ordering/checkout` | Minimal API lambda |
| `Handler` | `CheckoutHandler.HandleAsync` | Application layer |
| `Method` | `Order.MarkPaid` | Ara çağrılar |
| `Repository` | `OrderRepository.AddAsync` | Data access |
| `Entity` | `Order` | EF Core entity |
| `Table` | `ordering.orders` | EF Core `IModel` |
| `Column` | `ordering.orders.Status` | EF Core `IModel` |
| `Event` | `OrderPaid` | MassTransit contract |
| `ExternalCall` | `HttpEmbeddingService` | HttpClient invocation |

Ek alan: `RootKind` (`Endpoint` \| `Consumer` \| `BackgroundService` \| null) — kök olmak bir düğüm tipi değil, roldür.

### Edge tipleri

`CALLS` · `READS` · `WRITES` · `MAPS_TO` · `PUBLISHES` · `CONSUMES`

Her veri kenarı `mechanism` ve `evidence` taşır — bir tablonun graph'a **doğru sebeple mi** girdiği kenar bazında cevaplanabilir olmalı.

### Node şeması

```jsonc
{
  "id": "endpoint:POST /api/ordering/checkout",
  "kind": "Endpoint",
  "rootKind": "Endpoint",
  "displayName": "POST /api/ordering/checkout",
  "module": "Ordering",
  "filePath": "src/Modules/Ordering/.../OrderEndpoints.cs",
  "line": 22,
  "location": "src/Modules/Ordering/.../OrderEndpoints.cs:22"
}
```

`filePath` + `line` **zorunlu** — attribution bunun üzerine kuruluyor. Varsayılan değerler dahil her alan açıkça serialize edilir.

---

# Fazlar

Her faz bağımsız çalışır ve test edilebilir. Bir faz bitmeden diğerine geçilmez.

## ✅ Faz 1 — Roslyn'e ısınma *(tamamlandı)*

Solution 66/66 yükleniyor, `SemanticModel` çalışıyor, 4 bağımsız hata sinyali.

**Öğrenilen:** `MSBuildLocator.RegisterDefaults()` metodun ilk satırında olması yetmez — MSBuild tiplerine dokunan kod ayrı bir sınıfta, `[MethodImpl(NoInlining)]` ile olmalı. JIT, metot gövdesindeki tipleri metoda girerken resolve eder.

**MSBL001:** `Microsoft.Build.Framework`'ün iki kopyası locator'ın resolver'ını devre dışı bırakıyor — locator'ın engellemek için var olduğu hata, kendi paket zincirinden geliyor.

## ✅ Faz 2 — Call chain *(tamamlandı)*

25 endpoint Minimal API lambda'larından çıkarılıyor, çağrı zinciri recursive takip ediliyor, `OrderPaid` köprüsüyle `Ordering → Notification` geçişi kuruluyor.

**Öğrenilen:** Sembol kimliği **compilation başına**, solution başına değil. `OrderPaid` iki compilation'da farklı `ITypeSymbol`; `SymbolEqualityComparer` eşit görmüyor. Projeler arası eşleme yapan her sözlük tam nitelikli isim kullanmalı. Bu bug, modüller arası tek köprüyü sessizce hiç kurmuyordu.

**Ambiguous politikası:** tüm implementasyonlar eklenir, `ambiguous: true` işaretlenir. Ölçüldü: dekoratör ve koleksiyon enjeksiyonunda "tümü" doğru cevap; yalnız config anahtarıyla seçilenlerde aşırı-yaklaşım. Politika yanlış değil — üç farklı DI şeklini tek etiketle göstermek yanlış.

## ✅ Faz 3 — Graph + tablo/kolon *(tamamlandı)*

`graph.json`: 400 node, 841 kenar, 8 DbContext, 16 tablo, 97 kolon.

| Ölçüm | Sonuç |
|---|---|
| Tablo recall (EF içi) | %90 |
| Tablo recall (EF dışı / raw SQL) | **%0** |
| Kolon recall | %83 |
| Precision (tablo ve kolon) | **%100** |
| Backward traversal | %100 |

**Öğrenilen:** 110 test yeşilken graph üç yerde sessiz yanlış cevap veriyordu (`kind` alanı varsayılanlarda yazılmıyor, Column→Table kenarı yok, outbox erişilemez, orphan endpoint'ler). Testlerin bulamadığını `graph.json`'ı elle okumak buldu. **Faz 7'nin varlık sebebi bu.**

**Kayıp sessiz değil:** raw SQL noktaları diagnostics'te `file:line` ile duruyor. Graph "dokunmuyor" demiyor, "bakamadım" diyor.

Açık kalan maddeler: F2 (Redis/ExternalCall ontolojisi), F4 (owned navigasyon okuması), F5 (outbox kolonları), F6 (raw SQL — yapısal), F9, F10.

---

## ✅ Faz 4 — Deterministik API *(tamamlandı, LLM YOK)*

Beş uç, hiçbiri LLM çağırmıyor ve hiçbiri solution yüklemiyor. İstek süresi **0,4–1,9 ms**, bellek 652 KB dosya → 3,34 MB heap.

```
GET  /endpoints          25 endpoint: route, method, modül, konum
GET  /trace?node=...     forward — tablolar, kolonlar, R/W, event köprüleri
GET  /backward?node=...  backward — RootKind'a göre gruplu kökler
GET  /tables             16 tablo, şema ve modül ile
GET  /graph/stats        node/edge sayıları, diagnostics, üretim zamanı, okunan dosya yolu
```

**Performans kuralı:** `build` ~25 s sürüyor (66 proje design-time build); API'nin arkasında asla çalışmaz. Yalnız `graph.json` okunur, istek başına mtime + uzunluk kontrolüyle tazelenir, parse hatası son iyi snapshot'ı düşürmez.

**F10 tip seviyesinde çözüldü:** `dataLayer` backward'da `null`, `entryPoints` forward'da yok. Yanlış okuma konvansiyonla değil tiple engelleniyor.

**`limitations` alanı** iki kaynaktan hesaplanır — build diagnostics'inin dosya eşleşmesi + cevabın kendisinden türetilenler. "0 tablo" yerine *"bu akış `ProductVectorRepository.cs:60`'ta ham SQL kullanıyor, o tablo listede yok"* çıkar. Elle yazılmaz.

**`confidence` dört kova**, sayısal skor yok: `Direct` / `RowLevel` / `Inferred` / `SecondClass`. Ambiguous için sebep **uydurulmaz** — graph hangi DI şekli olduğunu kaydetmiyor, limitation bunu açıkça söyler.

### Tek satır üretim kodu yazılmadan bulunan üç hata

Hepsi bir varsayılanı seçmek için yapılan ölçümden çıktı:

1. **`Walk` erişilebilirlik filtresi uyguluyordu**, sunum filtresi değil — bir utility node'un arkasında kalan *utility olmayan* her şey sessizce düşüyordu. 16 backward sorgusunun 4'ünde bir kök kayboluyordu (`MigrateAndSeedHostedService`).
2. **`RootKind` dolu bir node utility etiketlenebiliyordu.** İnvariant eklendi: bir kök, tanımı gereği yardımcı olamaz.
3. **`graph.json` çıktı sırası deterministik değildi** — aynı kaynaktan iki build, küme olarak aynı ama 8 node / 40 kenar yer değiştirmiş halde. Tek alanlık değişiklik 216 satırlık diff üretiyordu, yani dosya elle okunamaz hale geliyordu. `GraphJson.Canonical` ile sabitlendi; `elapsedMs` dosyadan çıkarıldı (makine hakkında bilgi, graph hakkında değil).

> Faz 2'nin `NotificationProcessor`'ı "tesadüfen doğru"ydu; bu üçü "tesadüfen zararsız"dı.

**Ölçüm tuzağı:** ilk `/trace` ölçümü 138 ms gösterdi. PowerShell'in `Invoke-WebRequest`'i 109 KB gövdeyi nesnelere çeviriyordu; `HttpClient` ile 1,91 ms. Ölçüm aracı, ölçülen şeyin 70 katı gürültü üretmişti.

> **Durak noktası.** Burada kullanılabilir bir ürün var.

## ✅ Faz 5 — Dokümantasyon & görselleştirme *(tamamlandı)*

`flowlens docs -o out/` → **37 dosya**: 25 akış diyagramı + 10 modül dokümanı + 1 modül bağımlılık grafiği + index. 206 test, 37/37 byte-identical, 26/26 `mermaid.parse()`, 0 markdown ihlali.

### Ölçümle değişen beş karar

Bu fazda beş tasarım kararı ölçüm sonucu değişti — kaydedilecek olan kararlar değil, **ölçümün kararı değiştirmesi**.

| Karar | Önce | Ölçüm | Sonra |
|---|---|---|---|
| Subgraph ile modül kutulama | Q3'te makul ve ikna edici | 33 kenarın **12'si ilgisiz kutuyu kesiyor** | Reddedildi; modül etikete taşındı (`Cart · cart.carts`) |
| Sabit yön (LR) | Tek yön tutarlı olur | 896 px'i aşan **20/25** | En geniş fan ≤ 7 → `TD`, üstü `LR` → **6/25** |
| Yön eşiği değişkeni | Node sayısı | 12 node'lu iki diyagram zıt cevap veriyor | **En geniş fan** — node sayısı yanlış değişkendi |
| "Sığmıyor" notu | Koşullu, vekil eşikle | Jeneratörün renderer'ı yok, piksel ölçülemez | Koşulsuz, iddiasız satır + mermaid.live bağlantısı |
| Kardeş kenar sırası (`1..n`) | Adım numarası | 36 grubun **13'ü** aynı çağrı yerini paylaşıyor | Numara adım değil, **çağrı yeri** sırası |

### Kaynak sırası — kardeş kenarlar

Ölçüldü: alfabetik sıralama vakaların **%61'inde** kaynak sırasından farklı. Okuyucu soldan sağa okuyup kod sırası sanıyordu; tesadüfen doğru olabiliyordu ama garantisi yoktu.

- `CALLS` kenarları `callSite` (dosya, satır, kolon) taşıyor; sıralama buna göre
- Aynı çağrı yerini paylaşan kardeşler **aynı numarayı** alır — tekrarlanan numara kusur değil bilgi: "tek çağrı, iki implementasyona açılıyor"
- Numaralı adımların **%19'u koşullu** (ternary, if/else, switch, catch, `?.`, `&&`/`||`/`??` sağ tarafı) ve işaretleniyor
- Aynı hedefe birden fazla çağrı: **57/512 kenar**; ilk çağrı yeri sıralar, hepsi `callSites` listesinde
- Sayfa altında numaralı adım listesi, her adımda `dosya:satır`

**İddia sınırı:** numaralar kaynak kodda yazılma sırasıdır. Koşullu dallar ve döngüler nedeniyle çalışma sırası farklı olabilir; koşullu işaretli adımlar hiç koşmayabilir.

### Determinizm — Faz 4'ten farklı bir ders

Faz 4'ün dersi **çıktı sıralamasıydı**: aynı küme, farklı sırada yazılıyordu. Faz 5'inki bir katman derinde: **sıralamayı deterministik yapmak yetmez, keşfin kendisi deterministik olmalı.** Üretilen kümenin *kendisi* değişiyordu — `seen` kümesi bir düğüme hangi kenarın bağlanacağını keşif sırasına bırakıyordu. Çıktıyı sıralamak bunu gizlerdi, düzeltmezdi: sıralı ama yanlış bir diyagram hâlâ deterministik görünür.

mermaid.live bağlantıları **stored block** ile üretiliyor (deflate değil): deflate formatı belirlenmiştir ama çıktısı encoder'a bağlıdır, dolayısıyla aynı graph iki makinede iki farklı bağlantı üretip 25 sayfada sahte diff çıkarabilirdi.

### Modül bağımlılığı — dört kategori

Kural hedefin **katmanına** dayanır (node id'sindeki `ModularCommerce.<Modül>.<Katman>` segmenti), modül adına değil.

| Kategori | Kural | Ölçülen |
|---|---|---|
| Sözleşme çağrısı — meşru | Hedef katman `Contracts` | 10 |
| Event — meşru | `PUBLISHES`/`CONSUMES` | 3 |
| Doğrudan referans — ⚠ ihlal adayı | Hedef `Application`/`Infrastructure`/`Domain` | **2** |
| Shared — nötr | Hedef modül `Shared` | 204 (çizilmez) |

İhlal adayı iki kenarın ikisi de **ters yönde**: `Shared → Catalog` ve `Shared → Inventory` (seeder). Kural: `X → Shared` gizlenir, `Shared → X` gösterilir. Checkout'un 7 cross-module senkron çağrısının **7/7'si Contracts** — ihlal yok.

> Araç kuralı uygular, hüküm vermez: diyagram "ihlal" demez, "Contracts dışından doğrudan referans" der ve `file:line` verir.

### Doğrulama iki katman

- **Tekrarlanan kapı:** `mermaid.parse()` her koşuda 26/26. Sürüm tam sabit (`mermaid` 11.16.1, `jsdom` 30.0.1), `package-lock.json` commit'li. `node` yoksa test **fail eder**, sessizce geçmez.
- **Tek seferlik görsel:** PNG render + inceleme + mermaid.live bağlantısı. `mermaid-cli` repo bağımlılığı **yapılmadı** — bir `npm ci`'nin tarayıcı indirmesi "opsiyonel kapı" kararını bozardı.

**Ayrıca bulunanlar:** markdown lazy continuation kusuru (10 modül sayfasının 10'unda; parse hatası yok, mermaid kapısı görmez, testler görmez — dosyaları okurken bulundu), ve sezgisel taramanın seçim etkisi (18/18 sanılan oran, kesin ölçümde 13/36).

## Faz 6 — Triage Bot (deterministik)

**Amaç:** incident'ta "nereye bakacağım" sorusunu otomatikleştirmek. Yeni sistem değil — Faz 4'ün backward traversal'ının farklı bir girdiyle kullanımı.

1. Input: stack trace (veya exception type + method name)
2. Proje-içi en üstteki frame'i bul, graph'ta eşleştir
3. `Backward(symbolId)` → hangi endpoint / consumer / background job'dan ulaşılıyor
4. `git log --oneline -5 -- <filePath>` → ilgili dosyalardaki son değişiklikler
5. Çıktı: **incident report** — etkilenen akışlar, tablolar, son commit'ler, muhtemel şüpheliler

**SINIR — yapılmayacak:** otomatik branch açma, otomatik fix, herhangi bir git write işlemi. Çıktı bir rapordur, bot bir developer değildir. Gerekçe `docs/design-decisions.md`'ye yazılacak: alert storm'da loop riski, log'lardaki PII, review edilmemiş patch'in yarattığı sahte güven.

## Faz 7 — Eval set

**Bu adım opsiyonel değil.** Faz 3, 110 test yeşilken üç sessiz yanlış cevap üretildiğini gösterdi. Testler kodun çalıştığını doğrular; eval set **cevabın doğru olduğunu** doğrular.

- `evals/questions.json` — 20 soru, doğru cevaplar ModularCommerce kaynak kodundan **elle** çıkarılır, FlowLens çıktısına bakmadan
- Soru dağılımı: 12 kolay/orta akış, 4 event üzerinden modül geçen akış, 4 zor vaka (interface ambiguity, raw SQL, dinamik çağrı)
- **En az bir EF dışı modül (Discovery) örneklemde olmalı** — Faz 3'te bu atlandığı için iki kategori hiç ölçülmedi
- Metrikler: recall (öncelikli) ve precision, tablo ve kolon seviyesi ayrı, **EF içi ve EF dışı ayrı raporlanır** — tek bir ortalama, aracın nerede kör olduğunu gizler
- Başarısızlıklar kategorize edilir: reflection, dynamic dispatch, raw SQL, interface ambiguity, diğer
- **Meta-test:** eval set F1–F10'un her birini görünür kılmalı; kılmıyorsa eval set yanlıştır

Recall %100 çıkarsa şüphelen — eval set çok kolay demektir.

## Faz 8 — Doğal dil arayüzü (opsiyonel, izole)

**Amaç:** analistin endpoint adını bilmek zorunda olmaması. Doğruluğa hiçbir şey katmaz; konfor ve projenin tezinin kanıtı.

```
1. Soru → LLM #1 → { "target": "...", "direction": "forward|backward" }
2. target → graph'ta fuzzy match → node id            [C# kodu]
3. Faz 4'ün Forward/Backward'ını çağır                [C# kodu]
4. Sonuç → LLM #2 → analiste yazılmış cevap + citations
```

**İzolasyon (Bölüm 4'teki kural):** `FlowLens.Llm` ayrı proje, `FlowLens.Core` ona referans vermez, yapılandırmayla kapatılabilir, kapalıyken her şey çalışır.

**Kurallar:** LLM'e `graph.json`'ın tamamı verilmez. Her iddia `dosya:satır` taşır. Graph'ta olmayan hiçbir şey söylenmez. Eşleşme bulunamazsa uydurulmaz. API key user secrets'tan okunur.

---

## Faz sonrası (şimdi değil)

MCP server · incremental update (`git diff` ile subgraph yenileme) · CI entegrasyonu (PR'da otomatik impact comment) · web arayüzü · OpenTelemetry karşılaştırması (runtime trace ile static graph eşleştirme, reflection kayıplarını yakalama).

---

## Claude Code için çalışma kuralları

1. **Önce keşif, sonra plan, sonra kod.** Plan mode kullanılıyorsa plan onaylanana kadar kaynak kod değişmez.
2. **Faz atlama.** Faz N'in kabul kriterleri karşılanmadan Faz N+1'e geçme.
3. **Kapsam dışı listesine uy.** Bölüm 5'teki hiçbir teknoloji önerilmeyecek. Gerekli olduğunu düşünüyorsan önce gerekçeni yaz ve sor.
4. **Faz 8'e kadar LLM yok.** Faz 4-7'de LLM çağrısı öneren bir plan reddedilir.
5. **Küçük commit'ler**, açıklayıcı mesajla.
6. **Emin olmadığında sor.** ModularCommerce'in yapısı hakkında tahmin yürütme — kodu oku veya sor.
7. **Sabit sayı hardcode etme.** Ölçülen değerler dokümana yazılır, teste değil.
8. **Sessiz kayıp yasak.** Elenen, çözülemeyen veya atlanan her şey diagnostics'e `file:line` ile yazılır. Graph "dokunmuyor" ile "bakamadım"ı ayırt etmelidir.
9. **Doğruluk LLM'de değil kodda.** Bir bilgi deterministik olarak üretilebiliyorsa LLM kullanma.

---

## Kazanım özeti

| Alan | Kazanım |
|---|---|
| Compiler / static analysis | Roslyn AST + semantic model, call graph construction, symbol resolution sınırları |
| Veri modelleme | EF Core `IModel` metadata API'si, entity-table-column eşlemesi, satır düzeyi INSERT semantiği |
| Graph | Node/edge ontology tasarımı, kenar yönünün semantik sonucu, BFS forward/backward |
| Dokümantasyon üretimi | Diagrams as code, living documentation, C4 benzeri katmanlı görünüm |
| Değerlendirme | Eval set tasarımı, recall/precision ayrımı, hata kategorizasyonu, "doğru sebeple mi doğru" |
| Mimari yargı | Kapsam sınırlama, LLM izolasyonu, otomasyonun nerede durması gerektiği |
| Domain | Change Impact Analysis, program slicing, incident triage — endüstride adı olan problemler |