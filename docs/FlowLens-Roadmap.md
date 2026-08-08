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

## Faz 4 — Deterministik API (LLM YOK)

**Amaç:** analistin `dotnet run` çalıştırmak zorunda kalmaması. Faz 3 zaten soruyu %100 precision ile cevaplıyor; eksik olan tek şey erişilebilirlik.

**Performans kuralı:** `build` ~25s sürüyor (66 proje design-time build). Bu, API'nin arkasında **asla** çalışmayacak. API sadece `graph.json` okur; bir istek < 2 saniye.

```
GET  /endpoints          25 endpoint: route, method, modül, konum
GET  /trace?node=...     forward — tablolar, kolonlar, R/W, event köprüleri
GET  /backward?node=...  backward — RootKind'a göre gruplu kökler
GET  /tables             16 tablo, şema ve modül ile
GET  /graph/stats        node/edge sayıları, diagnostics, üretim zamanı
```

**Kabul kriteri:** beşi de çalışıyor, hiçbiri solution yüklemiyor, her response'ta `dosya:satır` ve `limitations` alanı var, graph yoksa açık hata (sessiz boş liste yok).

> **Durak noktası.** Burada kullanılabilir bir ürün var. Devam etmeden değerlendir.

## Faz 5 — Dokümantasyon & görselleştirme

**Amaç:** ekibe yeni katılan birinin "bu akış nereden nereye gidiyor" sorusunu, kimseye sormadan ve eskimeyen bir kaynaktan cevaplayabilmesi. Bu, projenin en geniş kitleye hitap eden çıktısı.

- **Mermaid diyagram üretimi** — endpoint başına akış şeması: Endpoint → Handler → Repository → Table, event köprüleri ayrı kenar tipiyle
- **Modül dokümantasyonu** — modül başına markdown: hangi endpoint'ler, hangi tablolar, hangi event'ler publish/consume ediliyor, hangi modüllere bağımlı
- **Modül bağımlılık grafiği** — hangi modül hangisine dokunuyor; mimari ihlaller burada görünür
- **Living documentation** — `flowlens docs -o docs/` her commit'te yeniden üretilebilir, elle bakım gerektirmez

**Kabul kriteri:** üretilen Mermaid GitHub'da render oluyor, her diyagram `dosya:satır` referansı taşıyor, çıktı `graph.json`'dan deterministik olarak üretiliyor (elle düzenleme yok).

**Neden burada:** Faz 4'ün API'si var, veri hazır, ve bu çıktı hem onboarding hem de blog/mülakat için en gösterilebilir artefakt.

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