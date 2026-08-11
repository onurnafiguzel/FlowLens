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

## ✅ Faz 6 — Triage Bot *(tamamlandı, deterministik)*

`flowlens triage --stack-trace <dosya>` → incident report. Yeni doğruluk kaynağı kurulmadı: Faz 4'ün `AnswerBuilder`'ı iki yönde çağrılıyor, üstüne `git log` ekleniyor. **275 test**, 4 gerçek fixture, 0 sentetik.

```
Girdi            exception tipi, mesaj, çerçeve sayısı
Graph            okunan graph.json YOLU + node/kenar sayısı
Repo             kök YOLU + nasıl bulundu + HEAD sha
Hata noktası     node + konum, ya da NEDEN yok
Çerçeveler       her çerçeve, hükmü, doğrulama tablosuyla
Giriş noktaları  backward → RootKind gruplu ("3 endpoint + 1 background job")
Aşağı akış       forward → tablolar, erişim, kolonlar
Bilinen sınırlar limitations + hata noktasının TAM SATIR isabeti
Son commit'ler   dosya başına git log --oneline -5 + kaç dosya / kaç satır
```

### Üç hüküm — "graph'ta yok" ile "çağrı yok" aynı şey değil

| Hüküm | Anlamı |
|---|---|
| **eşleşti** | node bulundu, id + `file:line` |
| **graph'ta yok** | proje namespace'i ama node yok — *"FlowLens bu çerçeveyi göremedi"* |
| **proje dışı** | framework / 3. parti, tek satırda özetlenir |

Ölçüldü: `src/` altındaki 300 dosyanın **147'sinin** hiç node'u yok. Yani üçüncü hüküm kozmetik değil, ana vaka. Faz 3'ün *"graph 'dokunmuyor' demez, 'bakamadım' der"* kuralının triage'daki karşılığı.

### Uygulama öncesi ölçülen üç şey (Adım 0)

**0a — async çerçeve biçimi.** Hatırlananın yarısı yanlış çıktı: async metotlar demangle ediliyor (`StrategyAsync`), ama async **lambda** edilmiyor (`<>c.<<RunAsync>b__0_0>d.MoveNext()`). Parametreler CLR kısa adıyla geliyor (`Int32`, `Single[]`), node id ise C# adıyla (`int`, `float[]`) — takma ad tablosu gerekti.

**0b — inlining çerçeve DÜŞÜRÜYOR.** Release'de üç çerçeveden ikisi silindi; kontrol grubu (`[MethodImpl(NoInlining)]`) ayakta kaldı, yani sebep gerçekten inlining. ModularCommerce'e etkisi: 255 metot düğümünün **97'si (%38) senkron ve risk altında** — tam da inline'a en uygun küçük yardımcılar (`Money.Add`, `Result.Failure`). Async zincirler bağışık.

Çözüm köprüyü genişletmek **değil**: `graph'ta yok` ikiye ayrıldı. Graph N ≥ 2 hop'luk bir yol biliyorsa *"atlanmış çerçeve olabilir"* denir ve yolun düğümleri yazılır. *"Graph şu yolu biliyor"* ölçülebilir bir olgu; *"inline edildi"* ancak olasılık. **L20.**

**0c — fixture merdiveni.** "Gerçek stack trace" iddiası nasıl karşılanacağı yazılmamıştı. Dört basamaklı düşüş kuruldu ve inilen basamak kaydedildi. Sonuç: **4 gerçek, 0 sentetik** — gerçek Postgres/pgvector container'ları üzerinden, ModularCommerce'in derlenmiş DLL'leri referans alınarak, **hedef repoya tek bayt yazılmadan**.

> Sentetik bir fixture "en az 2 gerçek" kriterine sayılmaz ve rapor bunu açıkça söyler.

İlk denemede hata `:37` yerine `:16`'ya düştü. Yığın izini düzeltmek yerine **ikinci bir fixture** eklendi (A2) — istenen satırı elde etmek için izi düzenlemek, sentetiği gerçek diye sunmak olurdu. Sonuç: 4 fixture'ın 2'sinde tam-satır isabeti gerçekleşmiyor ve rapor bunu iddia etmiyor.

### Ö5 — diyagram adım numarası kullanılamadı

Faz 5'in `FlowSteps` numaralarını yeniden kullanıp *"hata bu akışın 3. adımında"* demek planlanmıştı. Ölçüm çürüttü: hata çerçevesi genellikle diyagramda yok. `post-api-inventory-reservations.md` **1 adım** gösteriyor, hata ise daraltılan 20 ara çağrıdan birinde. Yerine **çerçeve doğrulama tablosu** geldi — yığın izi zaten çalışma yolunu veriyor, graph'ın kattığı şey onu *doğrulamak*, yeniden uydurmak değil.

### Sınır — çağrılabilir yüzeyle uygulanan kural

`GitLog` yalnız `rev-parse` ve `log` çıkarabiliyor. "Git'e yazmıyoruz" bir yorum değil, **çağrılabilir yüzeyin özelliği**. Gerekçeler `docs/design-decisions.md` D1–D4: alert storm'da geri besleme döngüsü, log'lardaki PII, review edilmemiş yamanın sahte güveni.

git başarısız olursa rapor **yine üretilir**, git bölümü hatayı yazar, exit 3. Graph tarafı git olmadan da geçerli; raporu tümden gizlemek elde olan doğru cevabı da atmak olurdu.

### Test doğruydu, popülasyon sessizdi

Mutasyon 2 (köprüyü 1 → 2 hop yapmak) **hiçbir testi kırmadı** — 50 testin 50'si geçti. Sebep: beş fixture'ın hiçbirinde tam iki gerçek hop uzaklıkta çerçeve çifti yoktu. Graph'ta bu şekilden **310 tane** var, ama aradaki düğümler senkron — 0b'nin inline edip düşürdüğü sınıf.

| | Faz 5 §11.6 | Faz 6 |
|---|---|---|
| Sebep | Test yanlış satırı koruyordu | Test doğru satırı koruyordu, tetikleyecek veri yoktu |
| Sorulacak soru | *"testim gerçekten neyi koruyor?"* | *"testimi tetikleyecek girdi elimde var mı?"* |
| Düzeltme | Doğru satırı mutasyona uğrat | Popülasyonu genişlet |

> **Kural:** bir mutasyon testi kırmıyorsa, önce testi değil **popülasyonu** sorgula. Eksik testi fixture'dan değil **graph'tan** seçerek yaz.

Gerekçe: fixture seti bir **örneklem**, graph **popülasyonun kendisi**. Örneklemden test vakası seçmek, örneklemin zaten içerdiği şekilleri test etmektir — tanım gereği hiçbir boşluk bulamaz.

**Bu kural Faz 7'nin doğrudan girdisi:** her eval sorusu için *"bu sorunun yakaladığı hata sınıfından graph'ta kaç örnek var?"* sorulmalı. Sınıf tek örnekliyse eval set o kategoriyi değil, yalnız o örneği ölçüyor.

**Ayrıca:** Faz 5'in markdown kapısı bu fazın koduna taşınınca **ilk koşuda düştü** — aynı sınıf lazy continuation hatası, yeni dosya. Kapı taşındığı için üretilir üretilmez yakalandı; Faz 5'te aynı hatayı dosyaları elle okuyarak bulmuştuk.

## ✅ Faz 7 — Eval set *(tamamlandı, LLM YOK)*

`flowlens eval -o evals/report.md` → **22 soru**, yedi eksende skorlanıyor. Beklenen değerler
ModularCommerce kaynağından elle çıkarıldı ve `questions.json` **runner yazılmadan önce**
commit'lendi. **294 test**, 0 atlanan; `graph.json` ve `out/` değişmedi.

### Ölçülen — hiçbir eksende %100 yok

| Eksen | Kapsam | Recall | Precision |
|---|---|---:|---:|
| tablo | EF içi | **%97,1** (34/35) | %100 |
| tablo | EF dışı | **%75,0** (3/4) | %100 |
| tablo — erişim (R/W) | — | **%83,8** (31/37) | — |
| kolon-yazma | EF içi | **%81,6** (133/163) | **%96,4** |
| kolon-yazma | EF dışı | **%75,0** (9/12) | %100 |
| kolon-okuma | EF dışı | **%0,0** (0/2) | — |
| kök | — | **%76,5** (26/34) | %100 |
| event | — | %60,0 (3/5) | %100 |
| dış depo | — | **%0,0** (0/5) | %0 |
| sınır kodu | — | %91,7 (11/12) | varlık iddiası |

**Kanıt skoru, üç kova:** `beklenen-mekanizmayla` **142** (%81,1) · `farklı-ama-geçerli` **0** ·
`bulunamadı` **33** (%18,9). Orta kova boş — F7 için ayrılan kova bu koşuda hiç dolmadı.

**Precision artık %100 değil.** Faz 3 *"precision %100"* diye kayıtlıydı; kolon precision'ı
**%96,4** — beş fazladan kolon, hepsi L21. Rakam düşmedi, **yanlış soruyla ölçülmüştü** (§L21).

`kolon-yazma` ve `kolon-okuma` **toplanmaz**: `ColumnsByTable` yalnız `Writes` kenarlarına bakıyor,
dolayısıyla okuma recall'ı yapısal olarak 0. Tek sayıya indirmek yazma recall'ını ilgisiz bir
sebeple aşağı çeker ve F9'un boyutunu gizler.

### Eval set'in kendi hata payı ölçüldü: 3 düzeltme / 13 doğrulama

Gerçekleşen her kaçırma, rapora yazılmadan önce kaynağa karşı çapraz kontrol edildi.

| | |
|---|---:|
| `oracle-doğrulandı` — kaçırma tool'a ait | **13** |
| `oracle-düzeltildi` — beklenen değer yanlıştı | **3** (önceki tur) |

Üç düzeltmenin üçü de **ayrı commit**, her biri düzeltmeyi çürüten `file:line`'ı mesajında
taşıyor. *"Beklenen değer çıktıya uydurulmuş mu?"* sorusu tek bir `git log` ile cevaplanıyor.

> Ölçüm aracının hata payı bir varsayım değil, **rapordaki bir satır**. Faz 4'ün *"ölçüm aracı
> ölçülenin 70 katı gürültü üretebiliyor"* dersinin bu fazdaki karşılığı.

### Eval set kendi iç tutarlılığını sınadı ve tutarsız çıktı

İlk koşunun *"öngörülmedi + gerçekleşti"* kutusundaki tek soru **Q19**'du. Kaçırma tool'da değil
**oracle'daydı**: Q19 `notification.processed_messages`'ın kök listesinde checkout'u beklemiyordu,
ama **Q01 aynı köprünün ileri yarısını zaten iddia ediyor** ve o iddia doğrulandı. Aynı köprü
hakkında iki soru iki farklı şey söylüyordu.

Bunu bulan şey FlowLens'in çıktısı değil, **soru setinin kendi içindeki çelişki** oldu. Faz 1'in
*"68 proje / doğrusu 66"* dersinin bu fazdaki karşılığı — o zaman hatayı bir insan fark etmişti.

Çelişki taraması sonradan **makineyle** yapıldı: 16 tablonun 6'sının iki yönü de soruluyor, çelişki
tekti. Kalan **10 tablo çapraz kontrol edilemiyor** — tutarlı oldukları için değil, sınanmadıkları
için sessizler.

### Kapıyı düzeltmeden önce yazmak bir yerine iki hata buldu

Q06 ilk koşuda *"öngörüldü, gerçekleşmedi"* kutusuna düştü, yani rapor **öngörünün yanlış
olduğunu** söylüyordu. Değildi: Q06 `F2`/`L17` öngörüyor ama `externalStores`'u hiç **iddia
etmiyordu** — cevapta oynayabilecek eksen yoktu. Öngörü yanlış değil, **ölçülemezdi**.

Kapı (`EveryPredictedFailureHasAnAxisThatCouldRealiseIt`) **düzeltmeden önce** yazıldı ve 22
sorunun tamamını taradı: **2 soru, 4 girdi** — Q06 *ve Q01*. Sonra yazsaydım Q06'ya göre
şekillenir, Q01 sessiz kalırdı.

> Faz 6'nın kuralının soru seti üzerindeki karşılığı: **eksik kapıyı bilinen vakadan değil,
> popülasyonun tamamından türet.**

### 3×2'nin granülerlik sınırı

Kutuların birimi **soru**, öngörü değil — bir öngörüyü belirli bir kaçırmaya bağlamak graph'ın
taşımadığı bir eşleme ister. Bunun bedeli ölçüldü: **L23 soru düzeyinde öngörülmüştü, kalem
düzeyinde değildi.** Q01 yedi sınır öngörüyor ve kaçırma gerçekleşiyor, yani kutu *"teyit"* diyor;
ama kaybolan `order_lines.UnitPrice`/`Currency` o yedinin **hiçbirine** girmiyordu. Tek tek atıf
raporun soru soru bölümünden elle yapılabiliyor, kutulardan yapılamıyor.

### Açılan dört yeni sınır

| | Ne | Nasıl bulundu |
|---|---|---|
| **L21** | `IdentityByDefault` kolonları `RowInsert`'e giriyor, EF onları yazmıyor | Oracle'ın 7. adımının bağımsız doğrulaması — gerçek Postgres'e karşı EF'in SQL'i |
| **L22** | Event köprüsü fiziksel yayın noktasına değil raise site'a bağlı | Q15'in beklenen değeri; **bug değil, Faz 2 kararının faturası** |
| **L23** | Owned koleksiyonun İÇİNDEKİ owned tipin kolonları node olmuyor | Q01'in oracle kontrolü; `ComplexProperty` ve üst düzey `OwnsMany` çalışıyor, kırılan yalnız iç içe olan |
| **L24** | `raw-sql` uyarısı geri sorularda yapısal olarak çıkmıyor | Q16'nın oracle kontrolü; eşleştirme anahtarı erişilebilirlik, ham SQL'in eksilttiği şey de o |

**L21'in dersi metrik seviyesinde:** Faz 3 precision'ı *"bu kolon migration'da var mı?"* diye
sormuştu; doğru soru *"bu akış onu yazıyor mu?"* idi. Aynı veriye bakan iki soru, iki farklı cevap.

Dört ders, dört faz, aynı aile:

| Faz | Yeşil görünen | Gerçekte |
|---|---|---|
| 5 | mutasyon testi kırmadı | test **yanlış satırı** koruyordu |
| 6 | mutasyon testi kırmadı | test doğruydu, **popülasyon** sessizdi |
| **7** | precision **%100** | metrik doğruydu, **soru** yanlıştı |
| **7** | öngörü *"gerçekleşmedi"* | öngörü yanlış değildi, **ölçülemiyordu** |

### Meta-test ve ölçülemeyenler

F1–F10 + L1–L24 = **34 satır, gerekçesiz boş satır yok.** Soru taşımayan yedi satırın yedisinde de
gerekçe yazılı: F10 (yapısal, tiple çözülmüş), L2, L8 (invariant), **L10** (4 site ölçüldü, cevap
düzeyinde etkisi yok), L12 (tek örnek), L14 (ortam), L20 (çalışma zamanı).

Popülasyonu **0** olan sınıflar da satır olarak duruyor, sessizce atlanmıyor: reflection ve
dynamic dispatch hedef repoda hiç yok.

### Sol-alt kutu boş — ve bu bir başarı değil

Düzeltmelerden sonra öngörülmeyen kaçırma kalmadı. Yani bu koşuda eval FlowLens hakkında sürpriz
bir şey **bulmadı**; kendi hakkında üç şey buldu. Kutunun boş olması, soruların yalnız öngörülen
şeyleri bulduğu anlamına da gelebilir — bir sonraki koşuda şüphelenilecek yer burasıdır.

### Faz 8'in girdisi — parite tasarımı

Her soru **iki alan** taşıyor: analistin sorabileceği doğal dil `question` ve çözülmüş `selector`.

```
Faz 7:  selector → AnswerBuilder → expected
Faz 8:  question → LLM#1 → selector' → AnswerBuilder → expected     ve ayrıca  selector' ↔ selector
```

Böylece iki hata kaynağı ayrışır: **hedefleme** (LLM yanlış node seçti) ve **aktarım** (LLM cevabı
bozdu). Fark yoksa *"LLM bilgi kaybetmiyor"* **ölçülmüş** olur, varsayılmış değil.

Yüzey paritesi zaten bir kapı: `SurfacesAgree` testi HTTP `/trace`'in `AnswerBuilder` ile aynı
tablo/kolon kümesini verdiğini her koşuda doğruluyor. *"Dar beli ölçmek hepsini ölçer"* bir gerekçe
değil, **doğrulanan bir olgu**.

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