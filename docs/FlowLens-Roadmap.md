# FlowLens — Code Flow & Impact Analysis Tool

> Bu doküman Claude Code'a context olarak verilmek üzere yazıldı.
> Kod yazmadan önce **"Claude Code için çalışma kuralları"** bölümünü oku.

---

## 1. Problem

Büyük ekiplerde iki manuel süreç var:

1. **İş analisti impact analizi soruyor.** "Ödeme akışına yeni bir provider ekleyeceğiz, hangi tablo/kolon etkilenir?" → bugün bir developer'ın 30 dakikasını alıyor, cevap kişiye göre değişiyor ve eksik kalabiliyor.
2. **Incident triage.** "İade butonu 500 dönüyor" → loglara bakılıyor, local'de debug ediliyor, hangi katmanın bozulduğu elle bulunuyor.

Her iki sorunun da ortak zemini aynı: **kod tabanındaki akışın makine tarafından okunabilir bir haritası yok.**

## 2. Çözüm

Tek bir çekirdek, üç tüketici:

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
    ├──► Analyst Bot    : "iade akışı hangi tablolara dokunuyor?"  (forward traversal)
    ├──► Triage Bot     : stack trace → etkilenen akış + son commit'ler (backward traversal)
    └──► Visualization  : Mermaid diagram (graph.json'ın render'ı, ayrı iş değil)
```

### En kritik mimari karar

**LLM kaynak değil, arayüzdür.**

| Katman | Sorumluluk | Deterministik mi |
|---|---|---|
| Extraction | Roslyn + EF Core metadata | **Evet** — ground truth |
| Storage | `graph.json` | Evet |
| Traversal | C# BFS/DFS | Evet |
| Interface | NL soru → parametre çıkarımı, sonucu özetleme | Hayır (LLM) |

Doğruluk C# kodunda üretilir. LLM yalnızca (a) soruyu parametreye çevirir, (b) sonucu insan diline çevirir. Her cevap `file:line` referansı taşımak zorundadır — analist doğrulayabilmelidir.

**Neden bu ayrım:** impact analizinde %95 doğruluk işe yaramaz. Yanlış kolon = eksik migration = production hatası. LLM'e "kodu oku ve söyle" dedirtmek non-deterministik ve doğrulanamaz bir sistem üretir.

## 3. Kapsam Dışı (bilinçli olarak yapılmayacak)

Bunlar MVP'de **yasak**. Çekirdek bitmeden hiçbiri eklenmeyecek:

- ❌ Graph database (Neo4j, Gremlin) — `List<Node>` + LINQ yeterli
- ❌ Vector DB / embedding / RAG — graph traversal semantic search'ten daha doğru
- ❌ Taint analysis, data flow analysis, points-to analysis
- ❌ Incremental cache, CI entegrasyonu
- ❌ Otomatik branch açma / otomatik fix / auto-merge
- ❌ MCP server, web UI
- ❌ Multi-repo desteği

Hepsi çalışan bir çekirdeğin üstüne sonradan 1-2 günde eklenir. Başta eklenirse çekirdek hiç bitmez.

## 4. Teknik kısıtlar

- **Hedef repo:** ModularCommerce (.NET 10, modular monolith, DDD, MassTransit + RabbitMQ Outbox/Inbox, EF Core, PostgreSQL)
- **FlowLens ayrı bir repodur.** ModularCommerce'in kaynak kodunu okur, ona hiçbir şey eklemez.
- **Proje tipi:** .NET 10 console app (Faz 1-3), sonra minimal API (Faz 4)
- **Ana paketler:** `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.Build.Locator`
- **Dil:** kod ve yorumlar İngilizce, dokümantasyon Türkçe

## 5. Veri modeli

Node ve edge tipleri **bilinçli olarak az tutulur.** Genişletme isteği gelirse önce sor.

### Node tipleri

| Tip | Örnek | Kaynak |
|---|---|---|
| `Endpoint` | `POST /orders` | Controller action / minimal API mapping |
| `Handler` | `CreateOrderCommandHandler` | MediatR/Application layer |
| `Method` | `OrderService.Cancel` | Ara çağrılar |
| `Repository` | `OrderRepository.Update` | Data access |
| `Entity` | `Order` | EF Core entity |
| `Table` | `orders` | EF Core `IModel` |
| `Column` | `orders.status` | EF Core `IModel` |
| `Event` | `OrderCreated` | MassTransit contract |
| `ExternalCall` | `HttpClient → payment-api` | HttpClient invocation |

### Edge tipleri

| Tip | Anlam |
|---|---|
| `CALLS` | A metodu B metodunu çağırıyor |
| `READS` | Entity/tablo okunuyor |
| `WRITES` | Entity/tablo yazılıyor |
| `MAPS_TO` | Entity → Table, Property → Column |
| `PUBLISHES` | MassTransit `Publish<T>()` |
| `CONSUMES` | `IConsumer<T>` implementasyonu |

### Node şeması

```jsonc
{
  "id": "Modules.Orders.Application.CreateOrderCommandHandler.Handle",
  "type": "Handler",
  "displayName": "CreateOrderCommandHandler.Handle",
  "module": "Orders",
  "filePath": "src/Modules/Orders/Application/CreateOrderCommandHandler.cs",
  "line": 42
}
```

`filePath` + `line` **zorunlu** — attribution bunun üzerine kuruluyor.

---

# Fazlar

Her faz bağımsız olarak çalışır ve test edilebilir. Bir faz bitmeden diğerine geçilmez.

## Faz 1 — Roslyn'e ısınma

**Amaç:** Roslyn'in nasıl çalıştığını hissetmek. Henüz graph yok.

**Yapılacaklar**
- Console app oluştur, `MSBuildLocator.RegisterDefaults()` ile başlat
- `MSBuildWorkspace` ile ModularCommerce.sln'i yükle
- Tüm `MethodDeclarationSyntax`'ları gez, `dosya:satır → metot adı` bas
- Yüklenemeyen proje varsa `workspace.WorkspaceFailed` event'ini logla

**Öğrenilecek iki kavram**
- `SyntaxTree` — kodun ağaç hali, isimler sadece metin
- `SemanticModel` — o ağaçtaki isimlerin hangi sembole bağlandığı

**Kabul kriteri**
- Solution hatasız yükleniyor, metot sayısı konsola basılıyor
- Modül başına metot sayısı raporlanıyor

**Bilinen tuzak:** `MSBuildLocator.RegisterDefaults()` **ilk satırda**, herhangi bir Roslyn tipine dokunmadan önce çağrılmalı. Aksi halde assembly load hatası alınır.

---

## Faz 2 — Tek endpoint'in call chain'i

**Amaç:** Bir endpoint'ten başlayıp çağrı zincirini recursive çıkarmak. Hâlâ JSON yok, konsol çıktısı yeterli.

**Yapılacaklar**
- Bir entry point seç (ör. `POST /orders`)
- Her method body'de `InvocationExpressionSyntax`'ları bul
- `semanticModel.GetSymbolInfo(invocation).Symbol` ile hedef metodu çöz
- Recursive takip et, `HashSet` ile cycle koruması koy, max depth parametresi ekle
- `Publish<T>()` görünce generic type argument'ı yakala → event adı
- Karşılık gelen `IConsumer<T>` implementasyonunu `SymbolFinder.FindImplementationsAsync` ile bul → **modüller arası köprü**

**Interface problemi**
DI yoğun kodda `IOrderRepository.Update` çağrısı interface'e çözülür, concrete tipe değil. MVP çözümü: `SymbolFinder.FindImplementationsAsync` ile tüm implementasyonları bul, birden fazlaysa hepsini ekle ve node'u `"ambiguous": true` işaretle. Bu bilinçli bir trade-off, dokümante et.

**Kabul kriteri**
- Seçilen endpoint için Endpoint → Handler → Service → Repository zinciri konsola basılıyor
- Publish edilen event ve onu consume eden handler zincire dahil
- Sonsuz döngüye girmiyor

---

## Faz 3 — Graph + tablo/kolon eşlemesi

**Amaç:** Çıktıyı yapılandırmak ve veritabanı katmanını bağlamak. **Bu faz bitince projenin asıl değeri hazır** — LLM olmadan bile soru cevaplanabiliyor.

**Yapılacaklar**

1. Faz 2 çıktısını `Node` / `Edge` modeline dönüştür, `graph.json` olarak yaz
2. EF Core metadata'sını bağla:

```csharp
var entity = dbContext.Model.FindEntityType(typeof(Order));
var table  = entity.GetTableName();
var column = entity.FindProperty(nameof(Order.Status))!.GetColumnName();
```

> **Not:** Tablo/kolon eşlemesini SQL parse ederek veya isim tahmin ederek yapma. `IModel` kesin bilgiyi veriyor. DbContext'i design-time factory ile örnekle, veritabanına bağlanma gerekmiyor.

3. Repository metodunda hangi entity'ye dokunulduğunu Roslyn'den çıkar (`DbSet<T>` erişimi, `Update/Add/Remove` çağrıları) → `WRITES` edge'i
4. Kolon seviyesi için: metot içinde set edilen property'leri yakala, `MAPS_TO` ile kolona bağla
5. Traversal API'si yaz:

```csharp
// forward reachability
IReadOnlyList<Node> Forward(string startId, int maxDepth);
// backward reachability  
IReadOnlyList<Node> Backward(string startId, int maxDepth);
```

Basit BFS. Graph database gerekmiyor — birkaç bin node'da `List` + LINQ milisaniyeler içinde döner.

**Kabul kriteri**
- `graph.json` üretiliyor, her node'da `filePath` + `line` var
- `Forward("POST /orders")` çağrısı etkilenen tabloları ve kolonları döndürüyor
- Çıktı elle doğrulandı: en az 3 endpoint için sonuçlar gerçek kodla karşılaştırıldı

---

## Faz 4 — Analyst Bot

**Amaç:** Analistin doğal dille soru sorabilmesi.

**Akış — LLM'e graph verilmiyor:**

```
1. Soru → LLM #1 → { "target": "refund", "direction": "forward" }   [structured output]
2. target → graph'ta fuzzy match → node id                          [C# kodu]
3. Forward(nodeId, depth) → 10-30 node'luk alt küme                 [C# kodu]
4. Alt küme → LLM #2 → analiste anlatılmış cevap + citations        [structured output]
```

**Yapılacaklar**
- Minimal API: `POST /ask { "question": "..." }`
- LLM #1: soru → hedef akış adı + yön. JSON schema ile constrained output, parse hatası olursa retry
- Fuzzy matching: endpoint adı, handler adı, modül adı üzerinden (Levenshtein veya basit contains yeterli)
- LLM #2: node listesi → Türkçe özet. **System prompt'ta zorunlu kural:** her iddia `dosya:satır` referansı taşır, graph'ta olmayan hiçbir şey söylenmez
- Eşleşme bulunamazsa "bulamadım, şunları kastetmiş olabilirsin" döndür — uydurma yok

**Kabul kriteri**
- "İade akışı hangi tablolara dokunuyor?" sorusu doğru tabloları döndürüyor
- Her cevapta dosya referansı var
- Var olmayan bir akış sorulduğunda dürüstçe bilmediğini söylüyor

---

## Faz 5 — Triage Bot + Eval

**Amaç:** İkinci tüketici ve projenin kanıtı.

### 5a — Triage Bot (1 gün)

Yeni sistem değil, **aynı graph'ın ters yönü.**

- Input: stack trace (veya exception type + method name)
- Stack trace'teki en üstteki proje-içi symbol'ü bul
- `Backward(symbolId)` → bu metoda hangi endpoint'lerden ulaşılıyor
- `git log -- <filePath>` ile ilgili dosyalardaki son commit'leri çek
- Output: **incident report** — etkilenen akışlar, dosyalar, son değişiklikler, muhtemel şüpheliler

**Sınır:** otomatik branch açma ve fix yazma yok. Çıktı bir rapordur, bot bir developer değildir. Nedenini dokümante et: alert storm'da loop riski, log'lardaki PII'nin LLM'e gitmesi, review edilmemiş patch'in yarattığı sahte güven.

### 5b — Eval set (aynı hafta)

**Bu adım opsiyonel değil.** Bu olmadan "çalışıyor" iddiası doğrulanamaz.

- 20 soru yaz, doğru cevapları **elle** belirle (hangi tablolar, hangi kolonlar)
- Tool'u çalıştır, karşılaştır
- İki metrik ölç:
  - **Recall** — kaçırılan tablo oranı (kritik: eksik kolon, fazla kolondan tehlikeli)
  - **Precision** — yanlış eklenen tablo oranı
- Kaçırılanları **kategorize et**: reflection, dynamic dispatch, string-based SQL, ambiguous interface

**Kabul kriteri**
- `evals/` klasöründe soru-cevap seti ve sonuç raporu var
- Başarısızlıklar kategorize edilmiş ve nedenleri yazılmış

---

## 6. Faz sonrası (şimdi değil)

Çekirdek bitince, her biri 1-2 gün:

- **Mermaid export** — `graph.json` → diagram, ~20 satır
- **MCP server** — tool'u Claude Code / Cursor'dan çağrılabilir hale getirir
- **Incremental update** — `git diff` ile sadece değişen subgraph'ı yenile (content-hash cache)
- **CI entegrasyonu** — PR'da otomatik impact comment
- **OpenTelemetry karşılaştırması** — runtime trace ile static graph'i eşleştir, reflection kaynaklı kayıpları yakala

---

## 7. Claude Code için çalışma kuralları

1. **Önce keşif, sonra plan, sonra kod.** Her fazın başında ilgili kodu oku ve bir plan sun; onay almadan implementation'a başlama.
2. **Faz atlama.** Faz N'in kabul kriterleri karşılanmadan Faz N+1'e geçme.
3. **Kapsam dışı listesine uy.** Bölüm 3'teki hiçbir teknoloji önerilmeyecek ve eklenmeyecek. Gerekli olduğunu düşünüyorsan önce gerekçesini yaz ve sor.
4. **Küçük commit'ler.** Her mantıksal adım ayrı commit, açıklayıcı mesajla.
5. **Emin olmadığında sor.** Özellikle ModularCommerce'in klasör yapısı, endpoint tanımlama şekli (controller mı minimal API mı), MediatR kullanımı ve DbContext yapılandırması konusunda tahmin yürütme — kodu oku veya sor.
6. **Test.** Her faz için en az bir integration test: bilinen bir endpoint'in bilinen bir çıktıyı ürettiğini doğrulayan.
7. **Doğruluk LLM'de değil kodda.** LLM çağrısı eklerken kendine sor: bu bilgi deterministik olarak üretilebilir mi? Üretilebiliyorsa LLM kullanma.

---

## 8. Kazanım özeti

Bu proje bittiğinde elde edilenler:

| Alan | Kazanım |
|---|---|
| Compiler / static analysis | Roslyn ile AST + semantic model üzerinde çalışma, call graph construction |
| Veri modelleme | EF Core `IModel` metadata API'si, entity-table-column eşlemesi |
| Graph | Node/edge ontology tasarımı, BFS traversal, forward/backward reachability |
| AI engineering | Structured output, constrained decoding, grounding & citation, LLM'i doğruluk kaynağı yapmama disiplini |
| Değerlendirme | Eval set tasarımı, precision/recall, hata kategorizasyonu |
| Mimari yargı | Kapsam sınırlama, over-engineering'den kaçınma, otomasyonun nerede durması gerektiği |
| Domain | Change Impact Analysis, program slicing, incident triage — endüstride adı olan problemler |
