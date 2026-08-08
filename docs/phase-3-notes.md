# Faz 3 — Graph + tablo/kolon eşlemesi (tamamlandı)

> Ölçüm tarihi: 2026-08-08 · Hedef: `ModularCommerce.sln` (66 proje, 48'i test dışı) · SDK 10.0.301
> **Yeni ön koşul:** hedef repo **derlenmiş** olmalı (Faz 1-2 gerektirmiyordu). Gerekçe §3.

---

## 1. Kabul kriterleri

| Roadmap kriteri | Durum | Kanıt |
|---|---|---|
| `graph.json` üretiliyor, her node'da `filePath` + `line` var | ✅ | `GraphJson.Validate` yazmadan önce doğruluyor; ihlalde dosya **hiç yazılmıyor** (exit 5) |
| Entity→Table, Property→Column EF Core `IModel`'den | ✅ | `EfProbe`; isim tahmini ve SQL parse yok |
| `Forward(nodeId, maxDepth)` / `Backward(nodeId, maxDepth)` | ✅ | `CodeGraph`, roadmap imzaları birebir |
| `flowlens build` graph üretiyor · `flowlens trace` basıyor | ✅ | §7 |
| Graph istatistikleri raporlanıyor | ✅ | node/edge tipi, **mekanizma**, ambiguous, utility, süre |
| Faz 2 testleri yeşil kalıyor | ✅ | 57 → **125 test**, 0 atlanan |
| Çıktı elle doğrulandı: en az 3 endpoint | ✅ | §6 — checkout, catalog create, catalog list |

---

## 2. Ölçülen sayılar

Faz 3 sonu, `phase3-validation.md` §8'in düzeltmeleri dahil.

```
32 kök: 25 endpoint · 3 consumer · 4 background service   (hepsi node'unda RootKind taşıyor)
415 node · 966 kenar · graph.json 637 KB

Node:  Method 166 · Column 97 · Repository 62 · Handler 27 · Endpoint 25
       Entity 17 · Table 16 · Event 4 · ExternalCall 1
Kenar: CALLS 512 · WRITES 302 · MAPS_TO 114 · READS 31 · PUBLISHES 4 · CONSUMES 3

Mekanizma: EfModelMapping 114 · RowInsert 109 · EntityConstructorAssignment 67
           PropertyAssignment 53 · DbSetProperty 47 · OwnedCollectionAdd 19
           SaveChangesInterceptor 7 · DomainEventRegistry 4 · ConsumerRegistration 3
           FluentChainHead 3 · HttpClientInvocation 1 · ExecuteUpdateSetProperty 1
           EntityConstruction 17  ← ikinci sınıf
           SaveChangesWithEntityParameter 10  ← ikinci sınıf

EF modeli: 8 context, 18 entity tipi, 16 tablo (1,3 s)
22 ambiguous · 15 utility (Shared)
```

**İkinci sınıf kenar oranı: 27/966 (%2,8).** Faz 5 eval set'i bu ikisini ayrı ölçmeli.

Doğrulama ölçümleri (`phase3-validation.md`): forward tablo recall **%82**, kolon recall **%83**,
precision **%100**; backward recall/precision **%100**.

> **Önceki sayılar** (denetim sonrası, satır düzeyi kural öncesi): 400 node · 841 kenar · 82 kolon ·
> WRITES 192 · MAPS_TO 99, kolon recall %66. Aradaki fark tek bir kuraldan geliyor — bir INSERT'in
> tüm kolonlarını saymak (§5.10).

### Performans — 3 koşu

| Aşama | Ortalama | Aralık |
|---|---:|---|
| Solution yükleme | 20,1 s | 19,6–20,8 |
| EF modeli okuma | 1,4 s | 1,1–1,8 |
| Graph inşası (yürüyüş + overlay) | 10,5 s | 9,2–11,6 |
| **`flowlens build` toplam** | **32,5 s** | 30,4–34,6 |
| **`flowlens trace` (graph.json)** | **~1,5 s** | süreç başlatma dahil; traversal'ın kendisi ölçülemeyecek kadar kısa |

Yükleme Faz 2'de 16,1 s ölçülmüştü, şimdi 20,1 s. Fark FlowLens'in artık EF Core + Npgsql +
ASP.NET Core referans kümesini de yüklemesinden geliyor — bu fazın bilinçli bedeli.

**Asıl kazanç trace tarafında:** Faz 2'nin canlı trace'i 23 s sürüyordu çünkü her sorgu solution'ı
yeniden yüklüyordu. `graph.json` üzerinden aynı soru **~1,5 s**, ve bunun neredeyse tamamı
`dotnet run` süreç başlatması. Faz 4'ün `POST /ask` API'si bunu gerektiriyor.

---

## 3. EF Core modeline erişim — üç tasarım denendi, ikisi elendi

`IModel` okumak hedefin derlenmiş assembly'lerini **bu sürece** yüklemeyi gerektiriyor. Bu, Faz 1'de
MSBL001 ile öğrenilen assembly kimliği problemini geri getiriyor, o yüzden tasarım ölçümle seçildi.

### Elenen 1 — `Assembly.LoadFrom`

.NET Core'da ayrı bir bağlam **değil**: `Default.LoadFromAssemblyPath` + o dosyanın klasörüne bakan
**catch-all `Default.Resolving` probe'u**. Tam bağımlılık kapanışı olan tek klasör Host çıktısı, ve
orası şunları taşıyor (`Host.csproj` → `EntityFrameworkCore.Design` → Roslyn):

| Assembly | Host bin | FlowLens bin |
|---|---|---|
| `Microsoft.CodeAnalysis(.CSharp/.Workspaces.MSBuild).dll` | **5.0.0.0** | 5.6.0.0 |
| `Microsoft.Build.Framework.dll` | **15.1.0.0** | *`Directory.Build.props` bilerek dışarıda tutuyor* |

Yani bu yol, MSBL001 düzeltmesini arka kapıdan iptal ederdi.

### Elenen 2 — tam izolasyon

`AssemblyDependencyResolver` shared-framework assembly'leri için **bilerek** `null` döner (deps.json'da
yokturlar, Default'la birleşsinler diye). İzole ALC'ye yüklenen EF Core
`Microsoft.Extensions.Caching.Memory` ister → ADR null → Default → FlowLens'in TPA'sında yok →
`FileNotFoundException`. "Süreçte EF Core'un tek kopyası olur" iddiası yanlıştı.

### Seçilen — hibrit ALC, iki listeli açık politika

`TargetModelLoadContext.Load` şunları `null` döndürür (→ Default → TPA):
`Microsoft.EntityFrameworkCore*`, `Npgsql*`, `System.*`, `netstandard`, `mscorlib`.
`Microsoft.Extensions.*` **önce Default'u dener, bulamazsa hedefin kopyasına düşer**.
Gerisi `AssemblyDependencyResolver` + kardeş dosya araması ile ALC'de izole kalır.

Bu, `typeof(DbContext)`'i sınırın iki yanında aynı `Type` yapar (kritik) ve birleştirilen küme tam
olarak FlowLens'in kendi paket listesi olduğu için **sürüm çakışması restore anında NU1605 ile**
yakalanır, çalışma anında sürprizle değil. MSBL001'den yapısal farkı bu: karara hiçbir çalışma-zamanı
resolver kancası karışmıyor.

**Değişmez kurallar:** `Assembly.LoadFrom` hiç kullanılmıyor · `Default.Resolving`'e hiç
dokunulmuyor · ALC collectible değil, koşu başına tek örnek (EF iç servis sağlayıcısını statik
cache'liyor).

### Probing kökü: modül `bin`'i değil, Host çıktısı

```
Ordering.Infrastructure/bin/Debug/net10.0/  → 11 dll, yalnız proje referansları. EF Core YOK.
Host/bin/Debug/net10.0/                     → 173 dosya: tüm modüller + EF Core + Npgsql + deps.json
```

Classlib projeleri NuGet varlıklarını çıktıya kopyalamaz. Modülün `OutputFilePath`'ini kök yapmak
`FileNotFoundException` ile biterdi.

### Seçilen tasarımın üç şartı

"Aynı süreçte hibrit ALC" kararı üç şarta bağlandı. Karşılanma biçimleri:

**1 — EF'e dokunan tüm kod tek sınıfın arkasında, dışarı yalnız serialize edilebilir snapshot.**
`EfProbe` (eski adı EfModelReader) ürünün **tek** `using Microsoft.EntityFrameworkCore` taşıyan
dosyası. Adı bilerek ileride ayrı process olacak bileşenin adı; sınıfın başındaki yorum taşımanın
dört adımını sayıyor. `EfProbeArchitectureTests` sınırı disipline değil **derleyicinin girdilerine**
bağlıyor: `src/FlowLens.Core` altındaki her `.cs` taranır, EF/Npgsql `using`'i olan tek dosya
`EfProbe.cs` olmalı. `EfModelContract` (JSON) sözleşmenin süreç sınırını **bugün** geçebildiğini
kanıtlıyor; `EfProbeContractTests` gerçek hedefin snapshot'ını her koşuda round-trip ediyor.

> Analizciler EF tip **adlarını** string olarak karşılaştırıyor (`"Microsoft.EntityFrameworkCore.DbContext"`
> vs Roslyn sembolü). Test bu yüzden ham metni değil `using` yönergelerini arıyor: adlar veri olarak
> dolaşıyor, tipler hiç dolaşmıyor — korunan düzen tam olarak bu.

**2 — Ön kontrol sessiz geçmiyor.** `EfPreflight`, `BeforeRead` (derlenmiş mi + sürüm) ve
`AfterRead` (her context okunabildi mi) olarak ikiye ayrılmış. Bloke durumda
`EfPreflightException` fırlatılıyor, `graph.json` **yazılmıyor**, exit **6**.

**3 — Sınır kayda geçti:** `known-limitations.md` **L14**.

### Sürüm kapısı — sert hata, uyarı değil

TPA basit isimle bağlar ve **sürümü umursamaz**: FlowLens'te daha eski bir EF olsaydı sessizce
bağlanır, sonra rastgele bir noktada `MissingMethodException` olarak patlardı. `EfVersionGate`
Host'un `deps.json`'ını okur (modülünkini değil — modül 10.0.4, Host 10.0.9 diyor) ve
"aynı major, FlowLens ≥ hedef" kuralını **build'i durdurarak** uygular.

Her bloke mesajı dört şey taşıyor: ne yanlış, iki somut değer/yol, **bu hatanın neden
kendiliğinden anlaşılmadığı**, ve tam çözüm. Üçüncüsü MSBL001 dersi.

**Elle kanıtlandı.** `FlowLens.Core.csproj`'daki EF sürümü geçici olarak 10.0.9 → 10.0.4
düşürülüp `build` koşuldu:

```
error: EF Core surum uyusmazligi - model okunamaz, graph yazilmadi.

  paket    : Microsoft.EntityFrameworkCore
  hedef    : 10.0.9     (…/ModularCommerce.Host.deps.json)
  FlowLens : 10.0.4.0   (src/FlowLens.Core/FlowLens.Core.csproj)
  fark     : FlowLens hedeften eski

  Neden burada duruyoruz: .NET'in TPA listesi assembly'leri BASIT ISIMLE eslestirir ve
  surumu umursamaz. Uyusmayan surum sessizce baglanir, sonra model kurulurken alakasiz
  bir noktada MissingMethodException veya TypeLoadException olarak patlar…

  Cozum: src/FlowLens.Core/FlowLens.Core.csproj icinde surumu yukseltin:
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.9" />
  Hedefin EF surumu FlowLens'inkiyle hizalanamiyorsa docs/known-limitations.md L14'e bakin.
```

Sonuç: **exit 6**, `graph.json` **diske yazılmadı**, süre **19 s** — yani yalnız solution
yüklemesi; 10 saniyelik graph yürüyüşü **hiç başlamadı**. Sürüm geri alındıktan sonra mutlu yol
bit-bit aynı (400 node, 748 kenar).

### Bloke etmeyen tek şey: bayat build

Zaman damgası karşılaştırması sezgisel bir ölçüt ve bayatlık yanlış model demek değil — hedefte şu
an gerçek bir bayatlık var (Cart modülünde 7 dosya) ama `CartConfiguration.cs` değişmediği için
tablo/kolon adları etkilenmiyor. Sezgisel bir ölçüt yüzünden aracı kullanılamaz yapmak yanlış
olurdu; ayrı bir uyarı satırı olarak basılıyor.

### Model kurulumu veritabanına dokunmuyor

`jsonb`, `OwnsMany(...).ToJson()`, `Property<uint>("xmin")` — hepsi statik tip-eşleme araması.
pgvector endişesi de çürütüldü: `CREATE EXTENSION` ve `UseVector()` `DiscoveryModule.cs:54,59`'da,
yani **Api** projesinin DI kaydında; `new DiscoveryDbContext(options)` onları hiç çalıştırmıyor.
Asla çağrılmıyor: `EnsureCreated`, `Migrate`, `CanConnect`, herhangi bir `IQueryable` enumerasyonu.

---

## 4. Faz 2'den gelen dört karar — sonuçları

### (A) L9 — constructor kenarları: dar biçimde AÇILDI

17 entity tipinin **16'sı** bir `DbSet` yazma sinyaliyle ya da sahibi üzerinden zaten yakalanıyordu.
Tek istisna **`ProductEmbedding`**: `DbSet<ProductEmbedding>` deklare edilmiş
(`DiscoveryDbContext.cs:17`) ama `src/` içinde hiç referans edilmiyor — tüm erişim raw SQL.

Ölçüm kararı verdi: `ObjectCreationExpressionSyntax` **yalnızca `IModel`'de karşılığı olan tipler
için** geziliyor. Sonuç: `POST /api/catalog/products` → `discovery.product_embeddings` ulaşılabilir.
DTO record'ları (`CheckoutResponse`, `ChargeRequest`) IModel'de olmadıkları için hâlâ dışarıda.

### (B) L8 — Shared.Kernel gürültüsü: ETİKETLENDİ, filtrelenmedi

Kural yapısal: `ProjectClassifier` modülü `Shared` ise `utility: true`. 400 node'un **15'i**.
`TraversalQuery.IncludeUtility` ile Faz 4 küçültebilir; graph'tan bilgi silinmiyor.

### (C) Her veri kenarı `mechanism` taşıyor

10 mekanizma, ikisi açıkça **ikinci sınıf** ve raporda öyle işaretleniyor. Test
`EveryDataEdgeRecordsHowItWasDerived` bunu invariant olarak sabitliyor.

### (D) FakePspClient: ExternalCall DEĞİL

Detektör yapısal: çağrılan üyenin declaring type'ı `HttpClient`/`HttpMessageInvoker`.
`FakePspClient` gövdesi `Task.Delay` + `Random.Shared.NextDouble()` — hiçbir şey süreçten çıkmıyor,
node yok. **`HttpEmbeddingService.cs:62`** (`httpClient.SendAsync`) gerçek çağrı, node var.

Aynı kod tabanında iki "dış sağlayıcı" soyutlaması, biri gerçek biri sahte, ve yalnız gerçek olan
node üretiyor — **doğru sebeple**. `ExternalCallsAreFoundByMechanismNotByName` bunu sabitliyor.
Checkout için "hangi dış servise gidiyor?" sorusunun doğru cevabı: **hiçbirine.**

---

## 5. Dört bug, dört ders

Hepsi **sessizce yanlış cevap** üretiyordu; hepsi elle doğrulama sırasında yakalandı.

### 5.1 `SaveChangesAsync`'in `ContainingType`'ı türetilmiş context değil

**Belirti:** `SaveChangesWithEntityParameter` hiç kenar üretmedi.
**Sebep:** `SaveChangesAsync` `DbContext`'in kendisinde bildirilmiş, dolayısıyla `ContainingType`
her zaman `"Microsoft.EntityFrameworkCore.DbContext"`. Hangi modülün modeli olduğu **yalnız
alıcının tipinde**. **Çözüm:** `GetTypeInfo(access.Expression)`. → 10 kenar.

### 5.2 Paylaşılan value object bir modülün tablosuna atfediliyordu

**Belirti:** Ürün oluşturmak `ordering.order_lines`'a ulaşıyordu.
**Sebep:** `Shared.Kernel.Money` Catalog'da complex property ama Ordering'de `OwnsOne` **entity**;
modelde `OrderLine`'ın owned tipi olarak görünüyor. Kimliğe bakan her modül — Catalog ürün yaratırken,
Payment tutar kaydederken — `ordering.order_lines`'a kenar alıyordu.
**Çözüm:** `IsAttributableEntity` — owned bir tip, sahibiyle **farklı modülde** bildirilmişse tek
başına bir tabloya atfedilemez. `OrderLine`/`PaymentAttempt` gibi sahibiyle aynı modüldekiler etkilenmiyor.

### 5.3 `table --MAPS_TO--> column` okumaları yazma gibi gösteriyordu

**Belirti:** `GET /api/catalog/products` altı kolon "yazdığını" iddia ediyordu.
**Sebep:** Kenar bir *eşleme* gibi okunuyor ama *erişilebilirlik* gibi davranıyor: tabloyu okuyan
her şey, o tabloyu yazan herkesin dokunduğu tüm kolonlara ulaşıyordu. Checkout'a
`payment.payments.RefundedAtUtc` bu yolla giriyordu — checkout `Refund()` çağırmıyor.
**Çözüm:** Kenar kaldırıldı. Kolonlar **yalnız** onları yazan metottan erişilebilir; kolonun tablosu
zaten kendi id'sinin ilk yarısı.

### 5.4 Constructor gövdeleri hiç analiz edilmiyordu

**Belirti:** `POST /api/catalog/products` **sıfır** kolon raporluyordu.
**Sebep:** Yürüyücü invocation takip ediyor, `new Product(...)` invocation değil — ctor hiç node
olmuyor. Ama bir aggregate'in kolonlarının çoğu ilk kez tam orada yazılıyor.
**Çözüm:** Bir metot `IModel`'deki bir tipi construct ediyorsa, o tipin ctor'ları da analiz edilip
kolon yazmaları **çağıran metoda** atfediliyor (ctor private, tek başına erişilemez —
`Product.Create` "catalog.products.Sku'yu ne yazdı?" sorusunun dürüst cevabı). → Kolonlar 38 → 82.

### 5.5 Kısmi model sessizce exit 0 veriyordu — revizyonun asıl bulgusu

**Belirti yoktu. Sorun tam olarak buydu.**

`GraphBuilder.ReadModelAsync` **her** EF problemini bir diagnostics satırına çevirip devam
ediyordu, `Phase3Commands` de `graph.json`'ı yine de yazıyordu:

| Senaryo | Eski davranış | Yeni |
|---|---|---|
| Sürüm uyuşmazlığı | 0 tablolu `graph.json` diske yazılır, exit 3 | Hiç yazılmaz, exit 6 |
| **8 context'in 7'si yüklendi** | **exit 0**, graph yazılır, bir modülün tüm tabloları sessizce eksik | Dur |
| Assembly çözülemedi | diagnostic, **exit 0** | Dur |
| `ReflectionTypeLoadException` | diagnostic, **exit 0** | Dur |

Ortadaki satır en tehlikelisi: tüketici iyi biçimli bir graph görür ve *"bu akış hiçbir tabloya
dokunmuyor"* sonucunu **tam güvenle** çıkarır. Yazılmış olan `EfModelReadResult.IsComplete`
özelliği hiçbir yerde çağrılmıyordu.

Bu, faz boyunca bulunan diğer dört bug'dan farklı: onlar yanlış kenar üretiyordu, bu ise
**doğru kenarların yokluğunu normal gösteriyordu**. Sessizlik en kötü hata biçimi.

### 5.6 Tablolar üretilmiş migration koduna atfediliyordu

**Belirti:** `cart.carts` → `20260717152814_InitialCartSchema.Designer.cs:39`.

**Sebep:** `ToTable("literal")` taraması tüm dokümanları geziyordu ve **migration'lar modelin
tamamını yeniden bildiriyor** — her tablo için bir `ToTable` içeriyorlar. İlk eşleşme kazandığı
için bazı tablolar konfigürasyona, bazıları migration snapshot'ına düşüyordu.

**Neden önemli:** `filePath` + `line`'ın tüm vaadi oraya gidip tarif edilen şeyi
değiştirebilmek. Üretilmiş bir snapshot'a yönlendirmek, okuyucuyu **düzenlememesi gereken** bir
dosyaya gönderir.

**Çözüm:** `/Migrations/`, `*.Designer.cs` ve `*ModelSnapshot.cs` taramadan çıkarıldı.
`TablesAreAttributedToConfigurationsNotToGeneratedMigrations` bunu sabitliyor.
Faz 3'ün `obj/` bayatlık yanlış pozitifiyle aynı sınıf: üretilmiş kodu kaynak sanmak.

### 5.7 `graph.json` denetiminden çıkan dört bulgu

Dosya elle denetlendi (`filePath`/`line` 400/400 temiz, dangling ref yok). Dördü de gerçek çıktı.

**a) `kind` varsayılan enum değerinde serialize edilmiyordu.** `WhenWritingDefault`, sıfır değerli
her alanı düşürüyordu: **25 Endpoint node'u ve 512 CALLS kenarı `kind` alanı olmadan** yazılıyordu.
Her tüketicinin "alan yoksa Endpoint demektir" gibi yazılı olmayan bir kuralı bilmesi gerekirdi —
hata değil, **güvenle yanlış okuma** üretir. `WhenWritingNull`'a çevrildi; ayrıca hiç kullanılmayan
`bridgeResolved` alanı modelden silindi (748 kenarda ölü veri taşıyordu).

**b) Column'ın tablosu yalnız id string'indeydi.** Traversal bir kolona ulaşınca tablosunu bulmak
için `column:ordering.orders.Status` ayrıştırmak zorundaydı — graph'ın yerine geçtiği şey tam
olarak bu. **82 kolonun 82'sine `Column --MAPS_TO--> Table` kenarı eklendi.**

> **Yön neden Column→Table?** Ters yön (§5.3'te kaldırılan `Table→Column`) bir *eşleme* gibi
> okunup *erişilebilirlik* gibi davranıyor: tabloyu okuyan her şey, onu yazan herkesin dokunduğu
> tüm kolonlara ulaşıyordu. Column→Table'ın böyle bir etkisi yok — yazandan kolona, oradan
> tabloya (zaten başka yoldan da erişilebilir bir düğüme) gidilir; tabloyu **okuyan** yine hiçbir
> kolona ulaşmaz. `GET /api/catalog/products` hâlâ 0 kolon raporluyor.

**c) Endpoint lambda gövdeleri hiç analiz edilmiyordu.** Overlay yalnız yürüyüşün ulaştığı *metot
sembollerini* geziyordu. Dev endpoint'leri `DbContext`'i doğrudan lambda içinde kullanıyor
(`context.Reservations…ExecuteUpdateAsync`, `context.NotificationLogs.AsNoTracking()`), dolayısıyla
**hiçbir veri kenarı üretmiyorlardı** — hiçbir şeye dokunmuyor gibi görünüyorlardı. Sessiz kayıp.
Düzeltildi; 3 endpoint artık tablolarına ulaşıyor.

**d) `GET /` gerçekten orphan.** Kaynakta doğrulandı (`Program.cs:79`): gövdesi
`Results.Ok(new { application, modules })`. Çağrı yok, tablo yok — doğru davranış.

### 5.8 `ordering.outbox_messages` erişilemiyordu — interceptor kuralı

Roadmap Bölüm 2'nin örnek çıktısı bu tabloyu bekliyor; graph'ta vardı ama checkout'tan
erişilemiyordu. Outbox satırını `DomainEventToOutboxInterceptor` **SaveChanges sırasında** yazıyor,
yani hiçbir handler ondan bahsetmiyor ve ona giden bir çağrı kenarı yok.

**Yapısal sınır değil, düzeltilebilirdi — düzeltildi.** Yeni mekanizma:
`SaveChangesInterceptor` (7 kenar).

**Uygulanan kural ve dayandığı varsayım açıkça:** `ISaveChangesInterceptor` implementasyonlarının
gövdeleri analiz edilir; yazdıkları entity'ler bulunur; bir interceptor'ın hangi context'e bağlı
olduğu **DI'dan değil EF modelinden** çıkarılır — entity'yi tam olarak bir context eşliyorsa o
context'e bağlı sayılır. İki context aynı entity'yi eşliyorsa **hiçbir şey iddia edilmez**.

DI kaydını (`AddInterceptors`, generic bir yardımcının içinde) okumak alternatifti; L11 tam olarak
bu tür konfigürasyon okumasını yapısal olarak güvenilmez kaydediyor. Model üzerinden kurulan bağ
denetlenebilir.

**Tek yönlü aşırı-yaklaşım:** interceptor yalnız eşlenecek domain event varsa satır yazar, yani
event üretmeyen bir `SaveChanges` outbox'a dokunmaz. Impact analizi için doğru yanlılık bu —
*yazılabilecek* bir tablo cevapta görünmeli.

Sonuç: checkout artık **12 tablo** raporluyor (önce 11).

### 5.9 Interceptor'ı kolon düzeyine taşımak bir KÖK kararıdır — B seçildi

`phase3-validation.md` F5'i ölçtü: interceptor kuralı tablo düzeyinde çalışıyor ama
`ordering.outbox_messages`'ın **4 kolonu** (`Id`, `Type`, `Content`, `OccurredOnUtc`) hiçbir
akışta görünmüyor. Atamalar `DomainEventToOutboxInterceptor.cs:59-64`'te duruyor.

İlk yazdığım düzeltme cümlesi şuydu: *"interceptor gövdesi ayrı bir kök olarak analiz edilir."*
**Bu cümle bir kapsam değişikliğini kaçak yoldan içeri sokuyor.** Faz 2'de kök kümesi
*Endpoint + Consumer + BackgroundService* olarak karara bağlandı; "ayrı kök" demek o kararı
sessizce geçersiz kılmak olurdu. Ayrıca §5.8'in kendi mantığıyla da çelişirdi.

**A — Interceptor'ı dördüncü bir kök sınıfı yap.**
Kök kümesi *"bir isteğin, mesajın veya zamanlayıcının başlattığı şey"* olmaktan çıkar,
*"koşan her şey"* olur. Interceptor bir giriş noktası değil: kimse ona *doğru* yürümez,
onu EF çağırır. Kök yaparsak `Backward` sorgularında bir outbox kolonunun cevabı
"interceptor" olur — oysa doğru cevap **o SaveChanges'i çağıran endpoint**. Triage bot için
bu, şüpheliyi kaybetmek demektir.

**B — Kök kümesi sabit kalır; interceptor'ın yazma kümesi `SaveChanges` çağrı sitesine iliştirilir.**
§5.8 zaten tam olarak bunu yapıyor — tablo kenarını context/entity üzerinden, akışın *içinden*
üretiyor. Kolon düzeyi bunun aynı şeklinin devamı: interceptor gövdesi **analiz edilir**
(kolon kümesini çıkarmak için) ama **yürünmez**; sonuç, o context'e ulaşan her `SaveChanges`
sitesine bağlanır.

**Karar: B.** Üç gerekçe:

1. **Analiz etmek ≠ kök yapmak.** Gövdeyi okumak için ona yürüyor olmak gerekmiyor.
   `EfProbe` de hedefin DbContext'lerini kök yapmadan okuyor.
2. **Cevabın sahibi doğru kalıyor.** `Backward("column:ordering.outbox_messages.Type")` altında
   checkout ve cancel görünür — Faz 5a'nın istediği cevap bu.
3. **Graph şişmiyor.** İliştirilen şey **yalnız kolon yazma kümesi**; interceptor'ın kendi çağrı
   alt ağacı (`mapper.TryMap`, `JsonSerializer.Serialize`) graph'a girmiyor. A seçeneğinde her
   biri düğüm olurdu ve hiçbiri kimsenin sorusuna cevap değil.

**Bedeli açıkça:** §5.8'in tek yönlü aşırı-yaklaşımı kolon düzeyine de taşınır — event üretmeyen
bir `SaveChanges` de outbox kolonlarını yazıyor görünür. Tablo düzeyinde kabul edilen yanlılık
(L15), kolon düzeyinde **dört kat** görünür olur. Impact analizi için hâlâ doğru yön, ama
`mechanism: SaveChangesInterceptor` etiketi bu iddiaları ayırt edilebilir tutuyor.

**Uygulanmadı** — Faz 3 kabul kriterleri karşılandı, bu bir genişletme. Kararın kendisi burada
kayıtlı ki uygulanırken tartışma yeniden açılmasın.

### 5.10 Kolon kümesi atamadan üretiliyordu — bir INSERT ise satır yazar

Doğrulamanın (`phase3-validation.md`) en büyük tek bulgusu. Kolon yazmaları
`AssignmentExpressionSyntax`'ten üretiliyordu; bu bir `UPDATE` için **doğru** ölçüdür
(`record.UpdatedAtUtc = ...` gerçekten yalnız o kolonu değiştirir) ve bir `INSERT` için yanlış:
EF entity'nin tüm eşlenmiş kolonlarını yazar, atansın atanmasın.

Kaçan kolonlar rastgele değildi — tam olarak **hiçbir C# ifadesinin adlandıramayacağı** olanlardı:

| Kayıp | Neden görünmüyordu | Şimdi nerede |
|---|---|---|
| `identity.users.Id` | base tipte *property initializer* (`= Guid.NewGuid()`), türetilmiş ctor gövdesinde yok | `Shared.Kernel/Entity.cs:7` |
| `cart.carts.Items` | `OwnsMany(...).ToJson()` kapsayıcısı; iki tarafın da `GetProperties()`'inde yok | `CartRecord.cs:5` |
| `order_id`, `payment_id`, `id` | gölge property, C# üyesi yok | entity bildirimi |

Graph'ta **`.Id` ile biten tek bir kolon node'u yoktu** — 82'nin hiçbiri.

**Uygulanan kural.** `EntityAccess.WritesWholeRow`: `Add*` (INSERT tüm kolonları listeler),
`Update*` (EF her property'yi Modified işaretler), ve mapped bir entity'nin inşası. Overlay o
tablonun **kalan** kolonlarına `mechanism: RowInsert` ile W kenarı üretiyor — 109 kenar.

Üç sınır: `IsRowVersion` hariç (`xmin`'i Postgres yazar) · atamayla adlandırılan kolon kendi kesin
kenarını korur · gölge kolonun konumu entity bildirimi (uydurma değil, açacağın dosya o).

**Sıra önemliydi:** `EfProbe` kapsayıcı kolonu üretmeden satır kuralı onu bulamazdı.

**Ölçülen etki:** kolon recall %66 → **%83**, INSERT yollarında %60 → **%84**, UPDATE yollarında
%90 → **%100**. Precision **%100 kaldı** — üç kanaryayla doğrulandı, en anlamlısı: cancel akışı
`ordering.orders`'ı güncelliyor (2 kolon) ama aynı gövdede `order_status_history`'ye satır ekliyor
(6 kolon). Kural iki şekli tek gövdede ayırt ediyor.

> **Bir isim geri geldi ve bu sefer doğru.** Checkout artık `payment.payments.RefundedAtUtc`'yi
> yazıyor gösteriyor. §5.3'te bu bir **hataydı**: okuma, `Table → Column` kenarı üzerinden yazma
> gibi görünüyordu. Şimdi checkout o tabloya gerçekten satır ekliyor ve INSERT cümlesi kolonu
> listeliyor. Aynı isim, farklı iddia — `mechanism` alanı tam olarak bunu ayırt etmek için var.

### Ayrıca: tasarım incelemesinin öngördüğü sızıntı gerçekleşti

3 Infrastructure assembly'sinde birer tip yüklenemedi
(`Microsoft.Extensions.Hosting.Abstractions`, ASP.NET Core paylaşılan çerçevesinde). EF'in
`ApplyConfigurationsFromAssembly`'si `ReflectionTypeLoadException`'ı **yutup tipleri sessizce
düşürüyor** — düşen bir `IEntityTypeConfiguration` = sessizce kaybolan bir tablo. `EfProbe`
`GetTypes()`'ı bizzat çağırıp `LoaderExceptions`'ı raporladığı için görüldü.
**Çözüm:** `FlowLens.Core` → `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.

### Ve: bayatlık dedektörü yanlış pozitif veriyordu

"En yeni `.cs`" taraması `obj/` altındaki üretilmiş `AssemblyInfo.cs`'leri sayıyordu, yani herhangi
bir proje derlenir derlenmez her build bayat görünüyordu. `obj/`+`bin/` hariç tutuldu. Kalan uyarı
**gerçek**: Cart modülünde 7 kaynak dosya Host.dll'den yeni (2026-07-18 vs 07-17).
`CartConfiguration.cs` **değişmemiş**, dolayısıyla cart tablo/kolon adları etkilenmiyor — ama uyarı
duruyor, çünkü doğru.

---

## 6. Elle doğrulama — üç endpoint

### 6.1 `POST /api/catalog/products` — 9/9 kolon

`Product.cs:29-41` özel constructor'ı: `Name`, `Description`, `Sku`, `Price`, `StockQuantity`,
`IsActive`, `CreatedAtUtc`, `UpdatedAtUtc`. `Price` bir `Money` complex property'si →
`price_amount` + `price_currency`. **Toplam 9 kolon.**

FlowLens: `catalog.products.{CreatedAtUtc, Description, IsActive, Name, Sku, StockQuantity,
UpdatedAtUtc, price_amount, price_currency}` — **birebir, eksik yok, fazla yok.**

Ayrıca `discovery.product_embeddings` (+3 kolon): `Product.Create` → `ProductCreated` raise →
registry → `ProductChangedConsumer` → `IndexProductHandler` → `ProductEmbedding.Create`.
Modüller arası köprü + L9 dar kuralı birlikte çalışıyor.

`Product.Update`'in kolonları **yok** — doğru, bu endpoint'ten erişilemiyor (graph'ta teyit edildi).

### 6.2 `GET /api/catalog/products` — kontrast vakası

Tablo: `catalog.products`. Kolon: **sıfır**. Doğru: bu bir okuma.
§5.3 öncesinde altı kolon iddia ediyordu.

### 6.3 `POST /api/ordering/checkout` — 11 tablo

```
cart.carts · catalog.products · inventory.reservations · inventory.stock_items
notification.notification_logs · notification.processed_messages
ordering.order_lines · ordering.order_status_history · ordering.orders
payment.payment_attempts · payment.payments
```

`notification.*` yalnızca `OrderPaid` köprüsünden erişilebilir (raise → outbox eşlemesi →
`IConsumer`) — Faz 2'nin köprüsünün tablo düzeyindeki karşılığı.
`ordering.orders.Status` + `UpdatedAtUtc`, `Order.TransitionTo`'nun (`Order.cs:166-167`) tek atama
noktası olması sayesinde sıradan `CALLS` kenarları üzerinden erişiliyor — ayrı bir yayılım
mekanizması gerekmedi.

### 6.4 Ters yön — `Backward("table:ordering.orders")`

Tam olarak **4 Ordering endpoint'i**: checkout, orders listesi, order detayı, cancel.
Başka modülden sızıntı yok (`BackwardFromTheOrdersTableFindsOnlyOrderingEndpoints`).

Faz 3 sonunda cevap bir satır daha kazandı: `ReservationTtlSweeper` de bu tabloyu okuyor ve artık
**arka plan işi olarak etiketli** görünüyor (F8 / L18-1). Doğru cevap her zaman buydu; eksik olan
sunumdu.

```
Entry points (5): 4 endpoints + 1 background job
```

> **Ölçüm sonrası not — dördü de doğrulandı.** §6'nın üç endpoint'i elle karşılaştırıldı, dördüncüsü
> (EF dışı, `POST /api/discovery/search`) sonradan eklendi, backward ayrıca ölçüldü. Rakamlar,
> gerekçeleri ve on farkın (F1–F10) tamamı `phase3-validation.md`'de.

---

## 6.5 İki komutun adı da "trace" — hangisi veri katmanını veriyor

İki kez yanlış okundu, o yüzden burada da yazılı. **Argüman hangisinin koştuğunu belirliyor:**
solution yolu Faz 2'nin canlı yürüyüşünü seçer (EF modeli yok → tablo/kolon yok), başka her şey
`graph.json` üzerinde gezinir.

| Komut | Başlık | Tablo/kolon |
|---|---|---|
| `flowlens build <solution> -o graph.json` | Phase 3 (graph build) | üretir |
| `flowlens trace "POST /api/ordering/checkout"` | Phase 3 (graph traversal) | **evet** |
| `flowlens trace "table:ordering.orders" --direction backward` | Phase 3 (graph traversal) | **evet** |
| `flowlens trace <solution> --endpoint "..."` | Phase 2 (call chain) | **hayır** |

**Faz 4'ün `GET /trace`'i ikinci ve üçüncü satırın karşılığıdır** — `graph.json` üzerinde
`Forward`/`Backward`, süreç başlatma dahil ~1,5 s. Solution yükleyen hiçbir yol API'nin arkasında
olmayacak (23 s'lik cevap bir API cevabı değildir).

İki düzeltme yapıldı, ikisi de yanlış okumanın kendisini hedefliyor:

1. **`--help` artık veri katmanı yolunu önce ve ayrı bir blokta gösteriyor**; Faz 1/2 komutları
   *"Everything below is an earlier phase kept for inspection. None of it reports tables or
   columns."* başlığı altında.
2. **Faz 2'nin canlı trace'i kendi kendini etiketliyor.** Koşarken başlığın hemen altına, doğru
   komutu hedefin kendi yoluyla birlikte basıyor:

```
FlowLens - Phase 2 (call chain)
      note: this is the live call-chain walk - NO tables or columns. For the data
            layer, build the graph once and trace that:
              flowlens build <solution> -o graph.json
              flowlens trace "POST /api/ordering/checkout"
```

Yardım metnine güvenmek yerine **koşan komutun kendisi** söylüyor — yanlış komutu çalıştıran kişi
yardım metnini zaten okumamıştır. Mod seçimi dört `CliOptionsTests` testiyle sabit
(`BareTraceOfARouteUsesTheBuiltGraph`, `TraceOverASolutionStillRunsTheLiveWalk`, …).

---

## 7. graph.json ve CLI

Node id formatı Faz 2'den **değişmedi**; dört yeni önek eklendi:

```
entity:ModularCommerce.Ordering.Domain.Orders.Order
table:ordering.orders
column:ordering.orders.Status
external:ModularCommerce.Discovery.Infrastructure.Embedding.HttpEmbeddingService
```

`table:` id'sinde şema **zorunlu** — bu repoda hem `catalog.outbox_messages` hem
`ordering.outbox_messages` var; şemasız id ikisini tek node'a çökertirdi.

```
flowlens build <sln> [-o graph.json]
flowlens trace "<node>" [--direction forward|backward] [--max-depth N] [--no-utility] [--graph <path>]
```

**`trace` varsayılan olarak graph'ı gezer**, `graph.json`'ı da varsayılan alır — roadmap'in kabul
kriteri harfi harfine `flowlens trace <endpoint>` diyor ve tablo/kolon taşıyan yol budur. İki mod
arasındaki seçimi **argümanın kendisi** yapıyor: `.sln`/`.slnx` ise Faz 2'nin canlı yürüyüşü,
değilse graph. `CliOptionsTests` bunu sabitliyor.

> İlk sürümde `--graph` zorunluydu ve `flowlens trace "POST /api/ordering/checkout"` hata
> veriyordu; kullanıcı `trace <sln> --endpoint …` biçimine düşüp tablosuz Faz 2 çıktısı alıyordu.
> Yetenek vardı, keşfedilebilirlik yoktu — kriterin harfi karşılanmıyordu.

Çıktı tabloları **konfigürasyon dosyalarına** atıfla, kolonları tablo altında gruplayarak ve
okuma/yazma ayrımıyla basıyor:

```
  Data layer - 11 table(s), 50 column(s):

    WR  ordering.orders       .../Configurations/OrderConfiguration.cs:13
          CreatedAtUtc, CustomerId, IdempotencyKey, Status, UpdatedAtUtc
    R   catalog.products      .../Configurations/ProductConfiguration.cs:11
```

`R`/`W` ayrımı kenarlardan türetiliyor, varsayılmıyor: yalnız sorgu üzerinden erişilen bir tablo
yazma gibi görünmemeli — migration gerekip gerekmediğine karar veren ayrım bu.

Yeni exit code'lar: `5` = graph invariant ihlali · `6` = EF modeli okunamadı/güvenilmez.
İkisinde de dosya **yazılmıyor**.

---

## 8. Testler

**142 test, ~65 saniye, 0 atlanan.** Faz 2'den devralınan 57'nin tamamı yeşil.

Son 17'si doğrulamanın kapattığı üç bulgunun (F1, F3, F8) regresyon kilidi. İkisi **popülasyon**
üzerinde iddia kuruyor, örneklem üzerinde değil — §10.1a'nın dersi: *"her kök `RootKind` taşır ve
başka hiçbir node taşımaz"* 415 node'un tamamı için doğrulanıyor.

Yeni: `EfProbeTests` (5, gerçek hedef, Roslyn'siz ~1 s) · `GraphTraversalTests` (7, sentetik) ·
`GraphJsonTests` (5) · `EntityAccessAnalyzerTests` (9, sentetik) · `Phase3IntegrationTests` (14) ·
**`EfPreflightTests` (8)** · **`EfProbeArchitectureTests` (2)** · **`EfProbeContractTests` (3)** · **`CliOptionsTests` (7)**.

Son üçü sınırın kendisini koruyor: bloke yollarının mesaj **içeriği** (sadece reddedildi mi değil),
EF referansının tek dosyada kalması, ve sözleşmenin süreç sınırını bugün geçebilmesi.

### Bulunan beşinci bug: suite kararsızdı

İlk tam koşudan sonra bir koşu **12 hatayla** düştü, sonraki koşu tekrar yeşildi. Sebep: xUnit
koleksiyonları varsayılan olarak **paralel** koşuyor ve Faz 3'ten itibaren hedef solution'ı
MSBuildWorkspace ile açan **iki** fixture var (`Phase2Fixture`, `Phase3Fixture`). Aynı solution'ın
eşzamanlı iki yüklemesi MSBuild build-host süreçleri ve hedefin `obj/` dizini üzerinde çakışıyor;
kaybeden taraf proje yükleme hatası raporluyor — yani Faz 2'nin testleri, Faz 2'de olmayan bir
sebeple kırmızıya dönüyordu.

**Çözüm:** `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
**Bedeli:** 31 s → ~85 s. **Gerekçesi:** kararsız bir suite yavaş olandan beterdir — insanlara
kırmızının "bir daha koş" demek olduğunu öğretir. Süreyi zaten iki solution yüklemesi domine ediyor.

**Sabit sayı kuralı korundu:** çapa route'lar, tablo adları ve invariant'lar; sayılar bu dokümanda.
Sentetik testler bilerek hedeften geniş — ModularCommerce `context.Add(entity)` ve `.Update(` hiç
kullanmıyor, dolayısıyla yalnız gerçek repoya bakan bir suite bu şekiller için boşuna yeşil olurdu.

---

## 9. Kapsam dışı (Faz 4)

`POST /ask`, LLM #1 (soru → parametre), fuzzy matching, LLM #2 (alt küme → Türkçe özet + citation).
Faz 3 hiçbir LLM çağrısı içermiyor ve içermemeli — doğruluk burada üretiliyor.

**Faz 4'e taşınan iki girdi:**
1. `TraversalQuery.IncludeUtility: false` — LLM'e gönderilecek alt kümeyi küçültmenin hazır kolu.
2. `mechanism` alanı — LLM'e "bu iddia birinci mi ikinci sınıf mı" bilgisini verebilir.

---

## 10. Faz 5 tasarım girdisi — denetimin dersi

§5.7'nin dört bulgusu ve `phase3-validation.md`'nin on farkı (F1–F10) **testler yeşilken**
üretilmişti: denetim anında 110 test, doğrulama anında 125 test, hepsi geçiyordu. Bu bölüm o
vakaların ortak sebebini ve Faz 5'in bunu nasıl yakalaması gerektiğini yazar.

> **Doğrulama kapsamı:** dört endpoint forward (biri bilerek EF dışı — `POST /api/discovery/search`),
> üç sorgu backward (iki tablo + bir kolon), ve ambiguous politikasının tek-konfigürasyon
> karşılaştırması. Backward recall/precision **%100** — Faz 5a'nın dayanağı ölçüldü.

### 10.1 Ortak sebep

**Dokuzu da bir YOKLUK'tu; testlerin hepsi bir VARLIK iddia ediyordu.**

Her test şu biçimdeydi: *"şu şekildeki koda karşı şu kenar üretilir."* Böyle bir test yalnız
**modellenmiş** bir yol hakkında soru sorabilir. Interceptor'ın yazması, endpoint lambda gövdesi,
owned navigasyon okuması, gölge kolon — bunlar üretim kodunun zihin haritasında yoktu; aynı
haritayla yazılan testlerde de yoktu. Test suite'i ile üretim kodu **aynı kör noktayı paylaşıyordu**,
çünkü aynı kişinin aynı modelinden çıkmışlardı.

Bunun üç somut görünümü:

**a) Örneklem vardı, popülasyon yoktu.** `Phase3IntegrationTests` birkaç çapa route ve tablo
üzerinde iddia kuruyordu. `kind`'ı olmayan 25 node ve 512 kenar, orphan kalan 4 endpoint,
kolonu olmayan 82 kolon — hiçbiri örneklemin içinde değildi. **400 node'un tamamı üzerinde bir
özellik iddia eden tek bir test yoktu.**

**b) Round-trip simetrik hatayı göremez.** `GraphJsonTests.RoundTripsNodesEdgesAndMechanisms`
aynı serializer ile yazıp okuyordu. Yazarken düşen alan okurken de yoktu; eşitlik iddiası geçiyordu.
Artefakt testi gibi görünen şey aslında bir **model** testiydi. Şimdi eklenen
`WritesKindEvenWhenItIsTheDefaultEnumValue` doğrudan **metne** bakıyor — fark bu.

**c) Doğrulama tek yönlüydü.** Her assert *"bu var mı?"* diye soruyordu. Hiçbiri *"bu neden yok?"*
diye sormuyordu. Oysa aracın ürettiği en tehlikeli cevap **boş küme**: "0 tablo", "hiçbir şeye
dokunmuyor". Doğru boş küme (`GET /`, `GET /api/catalog/products` 0 kolon) ile yanlış boş küme
(3 dev endpoint) **çıktıda birbirinden ayırt edilemiyordu**. Tüketici ikisini de tam güvenle okur.

Bir de üstü: graph.json bu dört hatayla birlikte **kendi içinde tutarlıydı.** Dangling referans
yoktu, 400/400 node konumluydu, şema geçerliydi. İçeriden bakan hiçbir kontrol onu yakalayamazdı —
çünkü tutarlılık, doğruluk değildir.

### 10.2 Hangi test tipi yakalardı

| Bulgu | Yakalayacak test tipi | Neden mevcut testler kaçırdı |
|---|---|---|
| **a** `kind` eksik | **Artefakt şema kontrolü** — serialize edilmiş *metne* karşı, *her* kayıt için zorunlu alan | Round-trip simetrikti (10.1b) |
| **b** Column→Table yok | **Graph invariant'ı** — her `Column` node'unun tam olarak bir `MapsTo` kenarı var | Kimse popülasyon üzerinde iddia kurmuyordu |
| **c** orphan endpoint | **Popülasyon invariant'ı + gerekçe zorunluluğu** — her `Endpoint` ya ≥1 giden kenar taşır ya da açık bir diagnostic | Örneklem 4 endpoint'i içermiyordu |
| **5.8** outbox erişilemiyor | **Diferansiyel test (oracle)** — bağımsız bir gerçeklik kaynağıyla karşılaştırma | Graph tutarlıydı; içeriden görünmez |
| **F1–F5** (validation) | Aynı: oracle | Aynı |

**Ayrım net:** üç bulgu **iç tutarlılık** hatasıydı ve ucuz, deterministik invariant'larla yakalanır.
Biri (5.8 — ve doğrulamadaki beşinin tamamı) **kapsam** hatasıydı: "interceptor diye bir yazma yolu
olduğunu hiç bilmiyorduk." Bunu hiçbir invariant bulamaz; yalnız **dışarıdan bir gerçeklik kaynağı**
bulur.

Faz 5 bu ikisini karıştırmamalı. Biri gate'tir, diğeri eval.

### 10.3 Faz 5 eval set tasarımı

Roadmap 5b bugünkü haliyle "20 soru, elle cevap, recall/precision" diyor. Bu tasarım **5.8'i ve
F1–F5'i yakalardı** (oracle var), ama **a/b/c'yi kaçırırdı** — 20 örnek, 400 node'luk popülasyonu
temsil etmez ve eval yalnız nihai cevaba bakar. Dolayısıyla eval set **iki katmanlı** olmalı:

**Katman A — invariant taraması.** Popülasyonun tamamı, her koşuda, milisaniyeler. Eval değil,
**gate**. Detayı 10.4'te.

**Katman B — oracle eval'i.** Örneklem, pahalı, elle veya çalışma zamanından. Roadmap'in 20 sorusu.

Katman B için altı tasarım kuralı — hepsi ölçümden çıktı, tahminden değil:

**1. Gerçeklik graph.json'dan okunmayacak.** Bu, aracı kendi çıktısıyla doğrulamak olur. İki
bağımsız kaynak: (a) kaynağın elle okunması — `phase3-validation.md` bunun bir şablonu; (b)
**çalışma zamanı SQL izi**: ModularCommerce'in kendi integration testleri gerçek Postgres'e karşı
koşarken bir `DbCommandInterceptor` her istek için dokunulan tabloları kaydeder. (b) mekanikleştirilebilir
ve tekrarlanabilir; (a) ölçek vermez ama (b)'nin kapsamadığı yolları (hata dalları, dev endpoint'leri) kapatır.

**2. Her soru bir "boş küme" ikizi taşıyacak.** *"X hangi tablolara yazar"* sorusunun yanına, doğru
cevabı **boş veya minimal** olan bir vaka: `GET /api/catalog/products` → 0 kolon, `GET /` → hiçbir şey.
Sessiz yanlışı ancak bu çevirir teste: 3 dev endpoint'i bu kural yakalardı, çünkü beklenen cevapları
boş **değildi** ama araç boş dönüyordu. **Recall'ı ölçmenin tek yolu, cevabı bilinen bir doluluk aramaktır.**

**3. Recall INSERT/UPDATE ayrı raporlanacak.** Doğrulama ölçtü: UPDATE yollarında kolon recall'ı
%90, INSERT yollarında %60. Tek bir toplam sayı (%70) bu ayrımı gizler ve yanlış yere iyileştirme
yaptırır.

**4. Kategoriler ölçümden gelecek, tahminden değil.** Roadmap dört kategori öngörmüştü: reflection,
dynamic dispatch, string-based SQL, ambiguous interface. **Doğrulanan üç endpoint bunların hiçbirine
denk gelmedi.** Gerçekte çıkanlar: `ef-side-effect`, `ef-implicit-read`, `ef-unnamed-column`,
`inherited-initializer`, `non-relational-store` (tanımları `phase3-validation.md`'de). Beşi de
*"kaynakta bir şey var ama sözdiziminde işaret edilecek yer yok"* ailesinden; roadmap'in dördü ise
*"sözdizimi var ama çözülemiyor"* ailesinden. **İkisi ayrı problem; eval set ikisini de sormalı** —
yoksa yalnız beklenen hata sınıfı ölçülür.

**5. Sonuçlar `mechanism` başına kırılacak.** Her veri kenarı zaten mekanizma taşıyor (karar C).
`EfModelMapping` 99, `EntityConstructorAssignment` 67, `PropertyAssignment` 52, `DbSetProperty` 47…
Precision/recall mekanizma başına kırılabilir. **Hacmi yüksek ama doğruluğu ölçülmemiş bir mekanizma,
bir sonraki bakılacak yerdir** — bu, eval'i sıralı bir iş listesine çevirir.

**6. "Bilmiyorum" ayrı puanlanacak.** Üç sonuç ayrılmalı: doğru / yanlış / **açıkça işaretlenmiş
belirsiz** (`Ambiguous`, `Truncated`, diagnostics). Sessizce kaçıran araçla sınırını bildiren araç
aynı puanı alırsa, araç sessiz kalmaya teşvik edilmiş olur — bu projede bugüne kadar bulunan her
ciddi hatanın tam olarak o davranış olduğu düşünülürse, metriğin bunu ödüllendirmesi gerekir.

**7. Eval set'in kendisi test edilecek — hazır meta-test.**

> **Eval set, `phase3-validation.md`'deki F1–F10'u yeniden bulmalı. Bulmuyorsa hata araçta
> değil, eval set'tedir.**

Bu, eval set için elde hazır duran bir doğrulama koşumu: on farkın her biri elle kanıtlanmış,
`file:line` ile konumlanmış ve kategorize edilmiş. Yeni yazılan bir eval set'i bunlara karşı
koşmak, "sorular doğru şeyi ölçüyor mu"yu araç değişmeden ölçer.

| Bulunmalı | Nasıl bulunur (eval set'te ne olmalı) |
|---|---|
| F1 `cart.carts.Items` | Kolon düzeyi soru + jsonb owned koleksiyonu olan bir tablo |
| F2 Redis | *"Bu akış hangi dış sistemlere yazıyor?"* — tablo sorusundan ayrı bir soru tipi |
| F3 `Id` + gölge kolon | INSERT/UPDATE ayrı raporlama (kural 3) |
| F4 owned navigasyon okuması | R/W ayrımı olan bir soru; yalnız "hangi tablolar" yetmez |
| F5 outbox kolonları | Interceptor'lı bir modülde kolon düzeyi soru |
| F6/F7 ham SQL | **EF dışı** bir modül (Discovery) örneklemde olmalı — yoksa kategori hiç ölçülmez |
| F8 kök tipi | Cevabı bir arka plan işi veya consumer olan bir backward sorusu |
| F9 kolon okuyucusu | *"Bu kolonu kim okuyor?"* — yazma sorusundan ayrı |
| F10 backward sunumu | Backward çıktısı ayrıca insan tarafından okunmalı, yalnız küme karşılaştırılmamalı |

Sağ sütun aslında **eval set'in tasarım şartnamesi**: her satır, olmazsa bir hata sınıfının
görünmez kalacağı bir soru tipi. F6 satırı en kritiği — ilk üç endpoint'in hepsi EF akışıydı ve
o seçim, roadmap'in dört kategorisinden ikisini ölçülemez kılmıştı (§10.3 kural 4).

### 10.4 Build sonrası invariant kontrolü — evet, iki seviyeli

**Cevap: evet, otomatik koşmalı.** Gerekçe doğrudan denetimden: bu dosyayı **bir insan elle okuyarak**
dört hata buldu. Bir insanın gözüyle tek geçişte bulunabilen şey, tek geçişlik bir kontrolle de
bulunur — ve `build` zaten dosyanın tamamını elinde tutuyor. Maliyet 400 node / 841 kenar üzerinde
tek geçiş: ölçülemeyecek kadar küçük, `build`'in 32 saniyesi yanında sıfır.

Ama hepsi aynı sertlikte olamaz. `EfPreflight`'ın ayrımı burada da geçerli (sürüm uyuşmazlığı
bloke eder, bayat build uyarır):

**Bloke edici — `graph.json` diske yazılmaz.** Bunlar "graph bozuk" demektir; bozuk bir graph'ı
teslim etmek, hiç teslim etmemekten kötüdür.
- *(mevcut)* boş `filePath`, `line ≤ 0`, dangling kenar
- her node ve kenarda `kind` alanı — serialize edilmiş **metin** üzerinde
- her `Column`'un tam olarak bir `MapsTo → Table` kenarı
- tekrar eden node id yok
- her veri kenarında (`Reads`/`Writes`/`MapsTo`) `mechanism ≠ None`

**Raporlayıcı — yazılır, ama çıktıda ADIYLA basılır.** Bunlar meşru olarak gerçekleşebilir, ama
sessizce gerçekleşemez.
- giden kenarı olmayan `Endpoint`'ler — **adları tek tek**
- hiçbir `Reads`/`Writes` kenarı almayan `Table`'lar
- hiçbir endpoint'ten erişilemeyen `Table`'lar

**Tasarımın tek kuralı:**

> **Meşru olarak ihlal edilebilen bir invariant, çıktıda ADI GEÇEN bir istisna üretmeli — sessizlik değil.**

`GET /`'in orphan olması doğru. Ama `build`'in *"1 orphan endpoint: GET /"* diye basması şart:
o satır 4'e çıktığı gün birileri görür. Denetimin bulduğu şeyi bulan aslında bu satırın yokluğuydu.

**Nerede koşacak:** invariant seti **kod** olmalı, dokümandaki bir kontrol listesi değil — tek
implementasyon, iki çağıran: (1) `Phase3Commands.BuildAsync` sonunda, `GraphJson.Write`'tan **önce**
(bloke ediciler için `GraphJson.Validate`'in doğal genişlemesi); (2) gerçek hedefin graph'ı üzerinde
koşan bir test. Katman A eval'i de aynı kodu çağırır — üç tüketici, tek gerçek.

**Bu, testlerin yerini almaz.** İç tutarlılığı garanti eder, doğruluğu değil (10.2). Kapsam
hataları yalnız Katman B'den çıkar.
