# Faz 4 — notlar

**Durum:** devam ediyor. Bu doküman ölçüldükçe doluyor.

---

## 1. API varsayılanı için yapılan ölçüm iki hata buldu

Faz 4'ün `includeUtility` varsayılanını seçmek için **25 endpoint forward + 16 tablo backward =
41 sorgu**, `IncludeUtility` true/false ile koşuldu. Tek satır API kodu yazılmadan iki hata çıktı;
ikisi de Faz 3 boyunca oradaydı ve 142 testin hiçbiri görmedi. Ayrıntı `known-limitations.md` L8.

**Hata 1 — bir kök yardımcı sayılıyordu.** `MigrateAndSeedHostedService`, `Shared` modülünde
bildirildiği için `utility: true` etiketliydi; oysa `catalog.products` ve `inventory.stock_items`'ı
tohumlayan bir **BackgroundService kökü**.
→ Invariant: `RootKind != None ⇒ utility = false` (`GraphBuilder` uygular, `GraphJson.Validate`
bloke edici olarak kontrol eder).

**Hata 2 — filtre erişilebilirliği değiştiriyordu.** `CodeGraph.Walk` utility node'u gezerken
atlıyordu, yani arkasındaki **utility olmayan** her şey de düşüyordu. Kökün etiketi düzeltildikten
sonra bile yol `IDataSeeder.SeedAsync` (Shared'da interface, kök değil, doğru şekilde utility)
üzerinden kesiliyordu.
→ Filtre **sonuca** taşındı (`CodeGraph.WithoutUtility`). Yürüyüş her zaman tam graph'ı geziyor.

### Ölçüm sonucu (41 sorgu, düzeltmelerden sonra)

| | tam | `--no-utility` | fark |
|---|---|---|---|
| Node | 1135 | 1009 | 126 gizlendi |
| **Tablo** | 48 | 48 | **0** |
| **Kolon** | 244 | 244 | **0** |
| **Kök (16 backward)** | — | — | **0** |

Düzeltmeden önce kök kaybı **4**'tü; dördü de aynı düğüm:

| Backward sorgusu | önce | sonra |
|---|---|---|
| `catalog.outbox_messages` | 3 | **4** |
| `catalog.products` | 5 | **6** |
| `discovery.product_embeddings` | 4 | **5** |
| `inventory.stock_items` | 6 | **7** |

**Karar: API varsayılanı `includeUtility=false`.** Filtre artık veri katmanında da kök seviyesinde
de bedava, ve bedava olması ampirik değil **yapısal** — `ThinningUtilityNodesNeverChangesWhatIsReachable`
testi 41 sorgunun tamamında utility olmayan node kümesinin birebir aynı kaldığını doğruluyor.
Her cevap ayrıca `filtered: { utility: N }` taşıyacak: gizlenen, kaybolan değil.

> **Neden dar kontrol kaçırdı:** filtre tek bir tablo veya kolon kaybettirmiyordu. Tablo/kolon
> seviyesinde bakan bir diff temiz okuyordu; kaybolan **kök**'tü. Faz 3'ün `graph.json` denetiminde
> öğrenilen dersin aynısı: yanlış sütuna bakan bir kontrol, sessiz kaybı doğrular gibi görünür.

---

## 2. `flowlens build` çıktı sırası deterministik değildi ✅ DÜZELTİLDİ

Aynı kaynaktan iki ardışık build, **küme olarak birebir aynı** ama **sırası farklı** bir
`graph.json` üretiyordu:

```
node kumesi  : 415 = 415, fark 0
kenar kumesi : 966 = 966, fark 0
yeri degisen : 8 node / 415, 40 kenar / 966
```

Etkisi: tek alanlık bir değişiklik `git diff`'te **216 satır** görünüyordu (2'si gerçek, 214'ü sıra
kayması). Yani `graph.json`'ın diff'i elle okunamaz hâle geliyordu — ki Faz 3'ün dört bulgusunu
bulan şey tam olarak dosyayı elle okumaktı. Faz 5'in *"aynı graph.json → byte-identical çıktı"*
kabul kriteri de rastgele sırayla hiç karşılanamazdı.

**Kaynak:** `SymbolFinder.FindImplementationsAsync` sabit bir sıra vaat etmiyor; implementasyonların
yürüme sırası node ekleme sırasını belirliyor. Düzeltme her üreticiye değil **tek çıkış noktasına**
konuldu: `GraphJson.Canonical`, `Write`'ın içinde.

### Sıralama anahtarları — tam ve kararlı

| | Anahtar | Neden tam |
|---|---|---|
| Node | `id` | Builder'ın sözlük anahtarı; yapı gereği tekil |
| Kenar | `fromId` → `toId` → `kind` → `mechanism` → `evidence` → `ambiguous` | Kenarın **taşıdığı her alan**. Anahtarda eşitlenen iki kenar birebir aynı kayıttır; sıraları bir baytı bile değiştiremez |
| Diagnostics | satırın kendisi | — |

### `elapsedMs` artık dosyaya yazılmıyor

Build süresini içinde taşıyan bir artefakt iki koşuda asla aynı baytları veremez. Alan
`GraphStats`'ta duruyor ve CLI hâlâ basıyor — makine hakkında bir bilgi, graph hakkında değil.
Tazelik isteyen tüketici dosyanın zaman damgasını okur (`/graph/stats` bunu zaten verecek).
`[property: JsonIgnore]`, şema kırılmadı.

### Doğrulama

```
run1 : DBFB0AFF98648F39...  652019 bayt
run2 : DBFB0AFF98648F39...  652019 bayt
BYTE-IDENTICAL : True
```

İki **gerçek** ardışık build byte-identical. Teste bağlanan iddia ise daha güçlüsü —
*"hangi sırayla gelirse gelsin aynı baytlar"*: `WritesTheSameBytesWhateverOrderTheGraphArrivesIn`
(sentetik) ve `TheBuiltGraphSerialisesIdenticallyWhateverOrderItIsIn` (gerçek 415/966 graph,
döndürülmüş kopya). Bu, iki koşunun ürettiği **iki** permütasyonu değil **tüm** permütasyonları
kapsıyor ve 32 saniyelik ikinci bir build gerektirmiyor.

**Bir kerelik bedel:** bu commit dosyanın tamamını yeniden sıralıyor, dolayısıyla diff 12.851 satır.
Bundan sonraki her diff yalnız gerçek değişikliği gösterecek.

**CLI çıktısı etkilenmedi:** 41 sorgunun 41'i sıralama değişikliğinden önce ve sonra
**byte-identical** (SHA-256). Beklenen sonuç — CLI kendi çıktısını zaten (derinlik, ad) ile
sıralıyor — ama varsayılmadı, ölçüldü.

---

## 3. API — ölçülen sayılar

### Bellek: 637 KB dosya → 3,19 MB heap

`GC.GetTotalMemory(forceFullCollection: true)` yükleme öncesi ve sonrası, ilk yüklemede bir kez
(her reload'da tekrarlamak çok pahalı, ve sayının bir kez dürüst olması yeter):

| | |
|---|---|
| `graph.json` | 652.019 bayt (637 KB) |
| Managed heap artışı | **3.348.768 bayt (3,19 MB)** |
| Oran | **5,1×** |

Planda "birkaç MB" tahmin edilmişti; **ölçüm 3,19 MB.** Tahmin doğru mertebedeydi ama artık tahmin
değil. 5,1× katsayısı beklenen: JSON'daki her string .NET'te ayrı bir nesne, artı `CodeGraph`'ın iki
adjacency sözlüğü (966 kenar iki kez indeksleniyor). Çalışma zamanında da görünür —
`/graph/stats` → `approximateHeapBytes`.

### İstek süreleri: 0,4–1,9 ms

20–30 koşunun medyanı, `HttpClient` ile (localhost):

| Uç | medyan | min | max | gövde |
|---|---:|---:|---:|---:|
| `/endpoints?module=Ordering` | **0,40 ms** | 0,36 | 1,74 | 1,3 KB |
| `/graph/stats` | **0,39 ms** | 0,34 | 0,44 | 3,3 KB |
| `/backward?node=table:ordering.orders` | **0,63 ms** | 0,57 | 0,99 | 19 KB |
| `/tables` | **0,97 ms** | 0,84 | 1,20 | 33 KB |
| `/trace?node=POST /api/ordering/checkout` | **1,91 ms** | 1,60 | 12,68 | 109 KB |

**2 saniyelik bütçenin ~1000 katı altında.** Süre gövde büyüklüğüyle doğrusal — yani maliyet
serializasyon, traversal değil. BFS 415 node üzerinde ölçülemeyecek kadar kısa.

> **Ölçüm tuzağı, kaydedilsin:** ilk ölçüm `/trace` için **138 ms** gösterdi ve `GraphSource`'un
> istek başına fazla iş yaptığını düşündürdü. Değilmiş — PowerShell'in `Invoke-WebRequest`'i
> 109 KB'lık gövdeyi nesnelere çeviriyordu. `HttpClient`'a geçince 1,91 ms. **Ölçüm aracının
> kendisi ölçülen şeyin 70 katı gürültü üretebiliyor.**
>
> `includeUtility=true` (1,79 ms) ile varsayılan (1,91 ms) arasındaki fark, `filtered.utility`
> sayısını üretmek için yapılan ikinci yürüyüşün maliyeti: **~0,1 ms**. Sessiz kayıp olmamasının
> bedeli bu.

### Beş çağrının çıktısı

`docs/flowlens-api.http` çalıştırılabilir hâlini tutuyor. Özet:

```
GET /endpoints?module=Ordering        → 4 endpoint, her biri file:line ile
GET /trace?node=POST /api/ordering/checkout
      direction=forward  traversed=181  maxDepth=20  truncated=false  filtered.utility=11
      dataLayer: 12 tablo, 62 kolon
      limitations: raw-sql(1) unmapped-column(3) second-class-evidence(15)
                   ambiguous-implementation(12) interceptor-columns(1)
GET /backward?node=table:ordering.orders
      traversed=35   dataLayer=null (F10 tiple çözüldü)
      entryPoints.total=5  →  Endpoint(4) + BackgroundService(1)
GET /tables                           → 16 tablo, erişim tüm graph üzerinden
GET /graph/stats                      → status=ok, 32 kök, 8 diagnostic, 4 duran sınır
```

**Kabul kriteri doğrulandı:** `/trace` checkout → **12 tablo, 62 kolon**;
`/backward` → **4 endpoint + 1 background job**, `RootKind`'a göre gruplu.

### `graph.json` bulunamıyordu — ve ilk düzeltme yanlıştı

İlk çalıştırmada beş ucun dördü **503** döndü. İlk teşhisim "content root yanlış" oldu ve çözümü
`ContentRootPath` yerine `Environment.CurrentDirectory` kullanmak diye yazdım. **Bu hiçbir şeyi
değiştirmedi** — ve düzeltmeyi test etmeden "düzeltildi" diye raporladım. Ölçülünce sebep görüldü:

```
currentDirectory = C:\...\FlowLens\src\FlowLens.Api
contentRoot      = C:\...\FlowLens\src\FlowLens.Api      ← ikisi AYNI
baseDirectory    = C:\...\FlowLens\src\FlowLens.Api\bin\Release\net10.0\
```

`dotnet run --project src/FlowLens.Api` çalışma dizinini **proje klasörüne** alıyor. Yani ikisi
arasında seçim yapmak anlamsızdı: graph iki dizin yukarıdaydı ve hiçbiri oraya bakmıyordu.

**Çözüm: sıralı arama** (`GraphPathResolver`), CLI'ın hedef solution'ı bulma yaklaşımının aynısı —
sürecin nasıl başlatıldığına değil, kodun nerede olduğuna dayan:

| Sıra | Nerede |
|---|---|
| a | `--FlowLens:GraphPath` ile verilen yol (mutlak veya göreli). Verilmişse **asla** başka yere düşülmez — operatörün adlandırdığı dosya bulunamadıysa sessizce başkasını okumak daha kötüdür |
| b | `Environment.CurrentDirectory` |
| c | `AppContext.BaseDirectory`'den **yukarı doğru**, `FlowLens.slnx` / `FlowLens.sln` / `.git` görülen yerde dur |

Ölçülen sonuç, repo kökünden **parametresiz**:

```
graph search: tried 7 path(s):
  ...\src\FlowLens.Api\graph.json
  ...\src\FlowLens.Api\bin\Release\net10.0\graph.json
  ...\src\FlowLens.Api\bin\Release\graph.json
  ...\src\FlowLens.Api\bin\graph.json
  ...\src\FlowLens.Api\graph.json
  ...\src\graph.json
  ...\FlowLens\graph.json                    ← bulundu
graph loaded from C:\...\FlowLens\graph.json: 415 nodes, 966 edges, ~3258 KB heap
```

**Üç şey ayrıca değişti, üçü de aynı dersten:**

1. **503 gövdesi denenen TÜM yolları listeliyor**, tekini değil — artı `pathDiagnostics` içinde
   `currentDirectory` ve `baseDirectory`. Tek bir yanlış yol, nereye baktığımızı söyler ama
   *neden oraya* baktığımızı söylemez; asıl soru odur.
2. **`/graph/stats` → `graphFilePath`.** Hangi dosyanın okunduğu görünmüyordu. Başka bir dizindeki
   bayat bir `graph.json`'a bakmak, her cevabı sağlıklı gösterip yanlış repo hakkında konuşmak
   demekti — sessiz hatanın ders kitabı örneği.
3. **Açılışta her zaman loglanıyor**, yalnız hatada değil. Bu karışıklığın tamamı iki dizinin
   birbirinden farklı olup hiçbir yerde görünmemesinden çıktı.

> **Asıl ders bende:** "düzeltildi" dedim, düzeltmeyi çalıştırmadım. Aynı fazda `Invoke-WebRequest`
> ölçüm tuzağıyla birlikte ikinci kez: **ölçmediğim şey hakkında konuştum.** Faz 3'ün dört sessiz
> bulgusunun kökü de buydu.

---

## 4. Tool'un hedef repoda bulduğu şeyler

Bunlar FlowLens'in hataları değil — **ModularCommerce hakkında** aracın ürettiği gözlemler.
Dördüncüsü Faz 4'ün utility ölçümünden çıktı.

| # | Bulgu | Nerede bulundu |
|---|---|---|
| 1 | **Survey 68/66** — solution'da bildirilenden fazla proje | Faz 1 |
| 2 | **Bayat build** — hedef, en yeni kaynak dosyasından önce derlenmiş | Faz 3 (`EfPreflight`, bloke etmeyen uyarı) |
| 3 | **`OrderCancelled` yayınlanıyor ama tüketicisi yok** | Faz 3 (diagnostics) |
| 4 | **Shared'daki bir background service `catalog.products`'a doğrudan yazıyor** | Faz 4 (utility ölçümü) |

### 4. hakkında

`MigrateAndSeedHostedService` (`Shared.Infrastructure/Persistence/MigrateAndSeedHostedService.cs:12`)
→ `IDataSeeder.SeedAsync` → `CatalogDataSeeder` / `InventoryDataSeeder` → `catalog.products`,
`inventory.stock_items`.

Yani **paylaşılan altyapı katmanındaki bir tip, modül tablolarına yazan bir akışın kökü.** Modüler
monolitte modül tablolarına yalnız o modülün kodu dokunur beklentisi varsa bu bir istisna.

**Seeder için kasıtlı olması çok muhtemel** — migration + seed'i tek yerden koşturmak yaygın ve
makul bir tercih, ve yazma işini yapan `CatalogDataSeeder`/`InventoryDataSeeder` zaten kendi
modüllerinde. Shared olan yalnız *tetikleyici*. Buraya bir kural ihlali olarak değil, **aracın
gösterebildiği bir mimari gözlem** olarak kaydedildi.

Faz 5'in modül bağımlılık grafiği bu tür geçişleri sistematik olarak görünür kılacak; bu, o
çıktının ne işe yarayacağının ilk somut örneği.
