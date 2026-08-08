# FlowLens — Bilinen Sınırlamalar

> Bu dosya, FlowLens'in **bilerek yakalamadığı** veya **yapısal olarak yakalayamadığı** şeyleri
> kaydeder. Amaç dürüstlük: eksik bir kenar sessizce kaybolursa, impact analizi "bu akış
> hiçbir tabloya dokunmuyor" gibi güvenle yanlış bir cevap üretir. Yanlış cevap, cevapsızlıktan
> tehlikelidir.
>
> Her madde şunu taşır: **ne kaçırılıyor**, **neden**, **hangi fazda ele alınacak**.

---

## L1 — Minimal API endpoint'lerinin tamamı lambda ✅ KAPANDI (Faz 2)

**Durum:** **Kapandı.** Faz 2'de `EndpointDiscovery` + `RoutePrefixResolver` ile çözüldü.

**Doğrulama (2026-08-07 ölçümü):**

```
25 endpoints · 0 unresolved route · 0 candidates eliminated · 0 multi-mount
pass 1: 25 map calls, 11 prefix propagations · pass 2: 19 methods reached
```

24 modül endpoint'inin **tamamı** doğru route'la bulundu (survey §2.2 ile birebir), artı
`Program.cs`'teki `GET /`. `POST /api/ordering/checkout` node'undan
`CheckoutHandler.HandleAsync`'e `CALLS` kenarı var. Beş adımlı prefix zinciri
(`Program.cs → IModule.MapEndpoints → 9 implementasyon → MapOrderEndpoints → MapGroup("") → MapPost`)
uçtan uca çözülüyor.

Test kapsamı: `RoutePrefixResolverTests` (7 test, sentetik) + `Phase2IntegrationTests` (gerçek repo).

> **Aşağıdaki bölüm tarihsel kayıt olarak korunuyor** — sorunun ne olduğunu ve nasıl çözüldüğünü
> anlatıyor.

### Bulgu

ModularCommerce'te **24 endpoint'in 24'ü** Minimal API lambda'sı olarak tanımlı. Tek bir
Controller, tek bir `[HttpPost]` attribute'u yok.

```csharp
// src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:22
secured.MapPost("/checkout", async (
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    ClaimsPrincipal user,
    CheckoutHandler handler,
    CancellationToken cancellationToken) =>
{
    var command = new CheckoutCommand(user.GetUserId(), idempotencyKey ?? string.Empty);
    var result = await handler.HandleAsync(command, cancellationToken);   // ← Endpoint→Handler kenarı BURADA
    ...
});
```

Faz 1'in tarayıcısı `MethodDeclarationSyntax` düğümlerini topluyor. Yukarıdaki `async (...) => {...}`
bir metot **bildirimi** değil, bir **ifade** — syntax ağacında
`ParenthesizedLambdaExpressionSyntax` olarak duruyor. Dolayısıyla:

- Faz 1'in raporladığı **399 production metodunun hiçbiri bir endpoint değil.**
- `Ordering.Api` için raporlanan 3 metot, `OrderingModule.Register` / `MapEndpoints` /
  `MapOrderEndpoints` — yani endpoint'leri *kuran* kod, endpoint'lerin *kendisi* değil.
- Lambda gövdesindeki `handler.HandleAsync(...)` çağrısı, hiçbir `MethodRecord`'a ait olmadığı
  için şu an bir sahibi olmayan çağrıdır.

### Neden bu bir hata değil, sınır

`MethodDeclarationSyntax` Faz 1'in kabul kriterini (metot sayısı + modül kırılımı) karşılıyor.
Lambda desteğini Faz 1'e sıkıştırmak, Faz 2'nin asıl işini — lambda'yı bir `Endpoint` node'una
bağlamak ve route'unu çözmek — faz sınırının yanlış tarafında yapmak olurdu.

### Faz 2'de yapılacaklar

1. `ParenthesizedLambdaExpressionSyntax` düğümlerini, **`MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapPatch`**
   invocation'larının argümanı oldukları yerlerde yakala.
2. Route'u iki parçadan birleştir — detay ve tuzaklar için
   [modularcommerce-survey.md §2.3](modularcommerce-survey.md).
3. Lambda'yı bir sözde-metot olarak ele al: `Endpoint` node'u = lambda, gövdesindeki
   invocation'lar onun `CALLS` kenarları.
4. Handler'ı lambda'nın parametre listesinden **çıkarmaya çalışma** — gövdedeki
   `handler.HandleAsync(...)` invocation'ını normal şekilde çöz. Daha az özel-durum, aynı sonuç.

### Çözümün iki kritik detayı (Faz 2'de öğrenildi)

1. **Extension method reduced/unreduced ayrımı.** `group.MapOrderEndpoints()` çağrı yerinde
   *reduced* forma (parametresiz), gövde içindeki `GetEnclosingSymbol` ise *unreduced* forma
   (`this` parametreli) bağlanıyor. Normalize edilmezse iki farklı anahtar olur ve **her modülün
   prefix'i kaybolur** — ilk çalıştırmada 24/25 route `unresolved` çıktı. Çözüm:
   `NodeId.Canonical` = `(symbol.ReducedFrom ?? symbol).OriginalDefinition`.
2. **Map çağrısının kendi relative prefix'i.** `endpoints.MapGroup("/api").MapPost(...)` gibi
   inline zincirlerde prefix, çağrının kendi `Origin`'inde durur. Pass 2'nin Parameter dalı önce
   bunu düşürüyordu; `RoutePrefixResolverTests` yakaladı. ModularCommerce yerel değişken
   kullandığı için gerçek repoda görünmüyordu — sentetik testin değeri tam olarak bu.

---

## L2 — `MethodDeclarationSyntax` dışındaki diğer bildirim biçimleri

**Durum:** Açık. Gerektiğinde ele alınacak (Faz 2/3).

Faz 1 sayımının dışında kalanlar:

| Biçim | Syntax düğümü | Neden önemli |
|---|---|---|
| Lambda | `ParenthesizedLambdaExpressionSyntax` | → **L1**, Faz 2'nin bloke edicisi |
| Constructor | `ConstructorDeclarationSyntax` | ModularCommerce primary constructor kullanıyor; bağımlılıklar orada |
| Local function | `LocalFunctionStatementSyntax` | Nadir, düşük öncelik |
| Property accessor | `AccessorDeclarationSyntax` | `DbSet<T> Orders => Set<Order>()` — **Faz 3 için önemli**, `WRITES` kenarının başlangıcı |
| Record generated üyeler | (syntax'ta yok) | `Equals`/`GetHashCode` — analiz için değersiz, bilinçli olarak dışarıda |

`DbSet` property'leri (`AccessorDeclarationSyntax`) Faz 3'te entity→tablo eşlemesi için
gerekecek. O zaman ele alınacak.

---

## L3 — Interface çağrıları implementasyona değil, contract'a bağlanır

**Durum:** Faz 2'de `SymbolFinder` ile ele alındı; **kalıcı olarak kısmi.**

**Faz 2 ölçümü** (checkout trace'i): 106 node, 185 kenar, **23 `SymbolFinder` çağrısı, 6 cache
isabeti**. Politika `AllImplementations` — belirsiz olan her node `ambiguous` işaretli.
Gözlenen belirsizlikler: `ICartRepository` (2), `IProductReader` (2), `IProductQueries` (2),
`IReservationStrategy` (3), `INotificationChannel` (3 — `Email`/`Webhook`/`FaultInjecting`).

Faz 1'in SemanticModel demo'su bunu ölçmüştü: `CheckoutHandler.HandleAsync` içindeki **49
invocation'ın 10'u** bir interface üyesine bağlanıyor.

```
syntax   : orders.GetByIdempotencyKeyAsync
semantic : ModularCommerce.Ordering.Domain.Orders.IOrderRepository.GetByIdempotencyKeyAsync(...)
                                                 ^^^^^^^^^^^^^^^^ interface, OrderRepository değil
```

DI'ın runtime'da hangi implementasyonu enjekte edeceği statik olarak bilinemez. Faz 2'nin MVP
çözümü `SymbolFinder.FindImplementationsAsync` ile tüm implementasyonları eklemek ve birden
fazlaysa node'u `ambiguous: true` işaretlemek.

**Bunun tamamen çözülemediği üç yer** (survey §7.4):

| Interface | Implementasyon sayısı | Neden belirsiz |
|---|---|---|
| `IReservationStrategy` | 3 (Naive / OptimisticConcurrency / RedisLock) | Hangisinin aktif olduğu `Inventory:ReservationStrategy` **config değerine** bağlı |
| `ICartRepository` | 2 (`PostgresCartRepository` + `CachingCartRepository` decorator) | Decorator zinciri; ikisi de gerçek |
| `IProductQueries` | 2 (`ProductQueries` + `CachingProductQueries` decorator) | Aynı |

Config'e bağlı seçim statik analizin **yapısal** sınırıdır. Faz 5'in eval setinde bu
kategori ayrı sayılacak.

---

## L4 — `Publish<T>()` generic çağrısı yok; event tipi runtime'da çözülüyor

**Durum:** **Çözüldü** (Faz 2), registry okuma stratejisiyle.

`DomainEventBridge` iki registry'den **4 domain→integration eşlemesi** okuyor
(`OrderPaid`, `OrderCancelled`, `ProductCreated`, `ProductUpdated`). Checkout trace'inde
`Order.MarkPaid --PUBLISHES--> Contracts.IntegrationEvents.OrderPaid --CONSUMES-->
OrderPaidNotificationConsumer.Consume` zinciri kuruluyor; kenar üzerinde `raiseSite` +
`mappingSite` kanıtı taşınıyor.

Roadmap Faz 2 "`Publish<T>()` görünce generic type argument'ı yakala" diyor. ModularCommerce'te
`src/` altında **generic `Publish<T>` çağrısı yok**. Her iki outbox dispatcher da tip-silinmiş
imzayı kullanıyor:

```csharp
// src/Modules/Ordering/.../Outbox/OutboxDispatcher.cs:100
await publisher.Publish(integrationEvent, clrType, cancellationToken);
//                                        ^^^^^^^ CLR tipi bir Dictionary'den runtime'da geliyor
```

Syntax'tan event tipi çıkmıyor. Çözüm: `IIntegrationEventMapper` implementasyonlarındaki
`typeof(...)` ifadelerini oku (survey §6.3). Registry sınıfı kendi yorumunda "tek genişleme
noktası (OCP)" diyor, yani bu okuma publish edilen event kümesini eksiksiz verir.

**Ek tuzak:** Generic `Publish` **testlerde var** (`OrderPaidPublishConsumeTests.cs:35`).
Test projeleri taranırsa sahte `PUBLISHES` kenarı üretirler. FlowLens test projelerini
`ProjectClassifier` ile ayırıyor; Faz 2'de graph'a test projeleri dahil edilmemeli.

---

## L5 — Design-time DbContext factory yok ✅ KAPANDI (Faz 3)

**Durum:** **Kapandı.** Options elle kuruluyor; factory gerekmedi.

8 DbContext'in tamamı tek parametreli primary constructor'a sahip (`DbContextOptions<TContext>`),
`OnConfiguring` override'ı yok ve tablo/kolon adları `IEntityTypeConfiguration` sınıflarının
**içinde** sabit. Dolayısıyla elle kurulan options **production ile birebir aynı** adları veriyor;
DI tarafında kaybedilecek bir isimlendirme yok (snake_case convention paketi de yok).

```csharp
var builder = (DbContextOptionsBuilder)Activator.CreateInstance(
    typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
builder.UseNpgsql(ProbeConnectionString, npgsql => npgsql.SetPostgresVersion(17, 0));
var context = (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
```

Connection string **iyi biçimli** olmak zorunda (`NpgsqlConnectionStringBuilder` onu hemen parse
eder), ama hiçbir bağlantı açılmıyor. `SetPostgresVersion` sabitlendi ki çıktı yalnızca kaynağın
fonksiyonu olsun, sağlayıcı varsayılanının değil.

**Discovery istisnası çürütüldü.** `CREATE EXTENSION vector` ve `NpgsqlDataSourceBuilder.UseVector()`
`DiscoveryModule.cs:54,59`'da — yani **Api** projesinin DI kaydında, DbContext'te değil.
`new DiscoveryDbContext(options)` onları hiç çalıştırmıyor ve Discovery de diğer yedisi gibi
sorunsuz örnekleniyor.

**Geriye kalan gerçek eksik:** `discovery.product_embeddings` tablosunun `vector(1536)` kolonu
migration içinde raw SQL ile ekleniyor ve `ProductEmbeddingConfiguration.cs:21`
`builder.Ignore(e => e.Embedding)` diyor — `IModel`'de yok, dolayısıyla graph'ta da yok. Tablonun
kendisi **var** (3 kolonuyla), yalnız embedding kolonu eksik. → L6.

**Faz 3'ün getirdiği yeni ön koşul:** hedef repo **derlenmiş** olmalı ve özellikle `Host` projesi
derlenmiş olmalı — modül `bin`'leri NuGet varlıklarını içermiyor, tam bağımlılık kapanışı yalnız
uygulama çıktısında. Detay: [phase-3-notes.md §3](phase-3-notes.md).

---

## L7 — Map fiili olmayan endpoint kayıtları bulunmuyor 🔒 KAPSAM KARARI (Faz 3)

**Durum:** **Kalıcı kapsam kararı olarak kapandı.** Kapsama 25/27 (%92,6) kalıyor.

Faz 3'te `graph.json` şeması sabitlenirken karara bağlandı. `MapHealthChecks` endpoint'lerini
eklemek için ya `(ANY, /health/live)` gibi bir **sözde-fiil uydurmak** ya da `HttpMethod` alanını
nullable yapmak gerekiyordu. İkisi de ontolojiyi, taşıdıkları bilgiden fazla kirletirdi:
`/health/live` ve `/health/ready` hiçbir handler'a, entity'ye veya tabloya gitmiyor —
`Forward()` onlardan boş küme döndürürdü.

Impact analizi ve triage için değeri sıfır olduğundan **bilerek dışarıda bırakıldı.** Sessiz kayıp
değil: sayı raporlanıyor ve fark burada yazılı.

> Aşağıdaki tarihsel kayıt sorunun ne olduğunu anlatıyor.
**Keşfedildiği yer:** Faz 2 ilk çalıştırması. **Faz 2 doğrulamasında detaylandırıldı.**

> **Doğrulama notu:** "Sözlüğe bir girdi ekle" göründüğü kadar basit değil. `MapHealthChecks`
> **tüm HTTP metotlarını** eşliyor — `MapGet` gibi tek bir fiile karşılık gelmiyor. Bir
> `Endpoint` node'u üretmek için ya `(ANY, /health/live)` gibi bir sözde-fiil uydurmak ya da
> `HttpMethod` alanını nullable yapmak gerekiyor. İkisi de veri modeli kararı; roadmap §5
> "genişletme isteği gelirse önce sor" diyor. Faz 3'te `graph.json` şeması sabitlenirken karara
> bağlanacak.

`EndpointDiscovery` yalnız `MapGet|MapPost|MapPut|MapDelete|MapPatch` fiillerini tanıyor.
ModularCommerce'in **`/health/live` ve `/health/ready`** endpoint'leri `MapHealthChecks` ile
kaydediliyor (`Shared.Infrastructure/Observability`), dolayısıyla graph'ta yok.

Bu sessiz bir kayıp **değil** — sayı raporlanıyor (25 bulundu, survey baseline'ı 24 modül
endpoint'i) ve fark açıklanabilir. Ama tam olmadığı kayda geçsin.

Aynı kategoride ele alınmayanlar: `MapMethods`, `MapFallback`, `MapHub` (SignalR),
`MapGrpcService`. Hiçbiri bu repoda yok.

**Çözüm maliyeti:** düşük — `MapVerbs` sözlüğüne girdi eklemek + `MapMethods` için fiil dizisini
argümandan okumak. Faz 3'te `graph.json` üretilirken ele alınabilir.

---

## L8 — Graph'ta Shared.Kernel gürültüsü ✅ KARARA BAĞLANDI (Faz 3)

**Durum:** **Etiketlendi, filtrelenmedi.** `utility: true` node attribute'u.

Kural **yapısal, isim tahmini değil**: node'un bildirildiği projenin modülü `Shared` ise
(`ProjectClassifier`), node `utility` işaretlenir. 400 node'un **15'i** bu kategoride.

Filtrelemek yerine etiketlemenin gerekçesi: filtreleme geri alınamaz bir bilgi kaybı ve
*"eksik, fazladan tehlikelidir"* ilkesine ters. Etiket kararı tüketiciye bırakıyor —
`TraversalQuery.IncludeUtility: false` Faz 4'ün LLM'e göndereceği alt kümeyi küçültür,
graph dosyası değişmeden.

Yeni bir `NodeKind` **değil**: `ambiguous`/`truncated` ile aynı kategoride bir attribute,
ontoloji büyümedi (roadmap §5).

> Faz 3 ölçümü, önceki tahmini düzeltiyor: checkout trace'inin 106 node'unun "önemli bir kısmı"
> denmişti; tüm graph'ta Shared oranı %3,75. `Forward()` tablo/kolon hedefine yürüdüğünde bunlar
> zaten yaprak kalıyor.

Checkout trace'inin 106 node'unun 89'u `Method` tipinde ve önemli bir kısmı
`Result.Success` / `Result.Failure` / `Error.Validation` gibi Shared.Kernel yardımcıları.
Bunlar **gerçek çağrılar** — uydurma değil — ama impact analizi açısından bilgi taşımıyorlar.

Faz 2'de filtrelenmedi çünkü "hangi çağrı önemsiz" kararı sezgisel ve roadmap'in
"eksik, fazladan tehlikelidir" ilkesine ters. Faz 3'te `Forward()` traversal'ı tablo/kolon
hedefine yürüdüğünde bu node'lar doğal olarak yaprak kalacak ve cevaba karışmayacak.

Framework/NuGet metotları zaten graph'a **girmiyor** (`SourceLocation.IsInSource` filtresi) —
`string.Join`, LINQ `Select`, `IValidator.ValidateAsync` gibi çağrılar dışarıda.

---

## L9 — Constructor çağrıları kenar üretmiyor ✅ KARARA BAĞLANDI (Faz 3) — dar biçimde açıldı

**Durum:** **`IModel`'de karşılığı olan tipler için açıldı; diğerleri kapalı kaldı.**

**Karar ölçümle verildi.** 17 entity tipinden **16'sı** zaten bir `DbSet` yazma sinyaliyle ya da
sahibi üzerinden yakalanıyordu. Tek istisna **`ProductEmbedding`**: `DbSet<ProductEmbedding>`
deklare edilmiş (`DiscoveryDbContext.cs:17`) ama `src/` içinde **hiç referans edilmiyor** — tüm
erişim `ProductVectorRepository`'nin raw SQL'i üzerinden, tek construction sitesi
`ProductEmbedding.cs:50`. Yani "repository çağrılarından çıkarmak yeterli mi?" sorusunun cevabı
17'de 16 evet, 1 hayır.

Uygulanan: `ObjectCreationExpressionSyntax` geziliyor **ama yalnızca yüklenmiş bir `IModel`'de
karşılığı olan tipler için**. Gürültü maliyeti ölçüldü — `CheckoutResponse`, `ChargeRequest`,
`PspResult` gibi DTO record'ları IModel'de olmadıkları için hâlâ dışarıda. Kenar `WRITES` ama
`mechanism: EntityConstruction` ile **ikinci sınıf** işaretli: construction persist değildir,
yalnızca entity'yi erişilebilir kümeye sokar.

**Beklenmedik ikinci kazanç:** ctor gövdeleri de analiz edilmeye başlandı. Yürüyücü invocation
takip ettiği için `new Product(...)` hiçbir zaman node olmuyor, dolayısıyla aggregate'lerin
kolonlarının çoğunun ilk yazıldığı yer görünmezdi — `POST /api/catalog/products` **sıfır** kolon
raporluyordu. Artık bir metot IModel'deki bir tipi construct ediyorsa o tipin ctor'ları da
analiz edilip kolon yazmaları **çağıran metoda** atfediliyor. Kolon sayısı 38 → 82.

> Aşağıdaki tarihsel kayıt Faz 2'deki durumu anlatıyor.

`CallGraphWalker` yalnız `InvocationExpressionSyntax` geziyor; `ObjectCreationExpressionSyntax`
kenar üretmiyor. Checkout zincirinde kaçırılanlar:

```csharp
new CheckoutResponse(...)   // CheckoutHandler.cs:207, 211
new OrderLineDraft(...)     // CheckoutHandler.cs:94
new ChargeRequest(...)      // CheckoutHandler.cs:134
new PspResult(...)          // FakePspClient.cs:35, 38
new NotificationInstruction(...) / new NotificationMessage(...)  // OrderPaidNotificationConsumer.cs:14, 19
```

**Faz 2'de somut kayıp yok — ölçüldü.** Kaçırılan constructor'ların **tamamı gövdesiz positional
record**; hiçbiri kod yolu gizlemiyor. Ayrıca gittikleri yer başka bir kenardan zaten görünüyor:
`new ChargeRequest(...)` Payment modülünü işaret ediyor ama `IPaymentService.ChargeAsync` kenarı
aynı bilgiyi taşıyor.

**Faz 3'te durum değişebilir.** Entity construction (`new Order(...)` deseni) tablo/kolon
eşlemesi için önem kazanırsa bu kapatılmalı. Maliyeti düşük (`ExpandInvocationsAsync`'e ikinci
bir döngü), bedeli gürültü: her DTO/record construction graph'a girer.

**Karar:** Faz 2'de dokümante edildi, Faz 3'te gerçek ihtiyaçla birlikte değerlendirilecek
*(kullanıcı kararı, 2026-08-07)*.

---

## L10 — `static readonly` alan referansları kenar üretmiyor

**Durum:** Açık. **DÜZELTİLEBİLİR**, düşük öncelik.
**Keşfedildiği yer:** Faz 2 doğrulaması, §5.1 / §5.2.

Hata sabitleri hem metot hem alan olarak tanımlanabiliyor ve FlowLens yalnız metot olanı görüyor:

```csharp
// OrderErrors.cs:7  - METOT -> kenar var
public static Error InvalidStateTransition(OrderStatus from, OrderStatus to) => ...
// OrderErrors.cs:13 - ALAN  -> kenar yok
public static readonly Error InvalidCustomerId = Error.Validation(...);
```

Kaçırılanlar: `OrderErrors.EmptyCart` (CheckoutHandler.cs:60),
`OrderErrors.DuplicateIdempotencyKey` (:175), `PaymentErrors.PspUnavailable` /
`.Timeout` (CardPaymentStrategy.cs:45, 50).

**Etkisi düşük:** ilgili sınıflar (`OrderErrors`, `PaymentErrors`) metot çağrıları üzerinden
graph'ta zaten var. Roadmap'in `CALLS` tanımı da ("A metodu B metodunu çağırıyor") alan okumasını
kapsamıyor — yani bu bir ihlal değil, kapsam kararı.

---

## L11 — Koleksiyon enjeksiyonu ve decorator zinciri modellenmiyor

**Durum:** Açık. **YAPISAL** (koleksiyon tarafı), decorator tarafı kısmen düzeltilebilir.
**Keşfedildiği yer:** Faz 2 doğrulaması, §9.

FlowLens interface çağrısını "tüm implementasyonlar" olarak açıyor. İki desende bu **doğru cevabı
yanlış sebeple** üretiyor:

**Koleksiyon enjeksiyonu.** `NotificationProcessor` kanalları `IEnumerable<INotificationChannel>`
alıyor (`NotificationProcessor.cs:16`) ve `foreach` ile geziyor (`:38`). DI'da **2 kayıt** var
(`NotificationModule.cs:32,38`) — her biri `FaultInjectingChannel(EmailNotificationChannel)` ve
`FaultInjectingChannel(WebhookNotificationChannel)`. FlowLens **3 kardeş implementasyon**
listeliyor. Üç tip de runtime yolunda olduğu için sonuç doğru — **ama tesadüfen.**

Aynı desen `PaymentService` → `IEnumerable<IPaymentMethodStrategy>` (`PaymentService.cs:23`);
şu an tek implementasyon olduğu için görünmüyor.

**Decorator zinciri.** `ICartRepository` kaydı yalnız `CachingCartRepository`
(`CartModule.cs:30` factory); `PostgresCartRepository`'ye onun **içinden** ulaşılıyor. FlowLens
ikisini kardeş sanıyor. Tip kümesi doğru, sarmalama ilişkisi yanlış. Aynısı `IProductReader`
(`CatalogModule.cs:59`) ve `IProductQueries` (`CatalogModule.cs:52`) için.

**Neden yapısal:** hangi implementasyonların koleksiyona kaydedildiği, DI kayıt kodunun
çalıştırılmasını gerektiriyor — L3'ün aynı kökü.

**Faz 5 için kritik:** eval set'i "sonuç doğru mu" ile "doğru mekanizmayla mı" ayrı ayrı ölçmeli.
Somut kırılganlık: `INotificationChannel` kanallarından biri DI'dan kaldırılırsa FlowLens yine
üçünü listeler — recall düşmez, precision düşer, test fark etmez.

---

## L12 — Delegate üzerinden yapılan çağrılar metinsel olarak yakalanıyor

**Durum:** Açık. Kabul edilen yaklaşım.
**Keşfedildiği yer:** Faz 2 doğrulaması, §5.3.

`CardPaymentStrategy.ExecuteAsync` (`CardPaymentStrategy.cs:33-34`), `ChargeOnceAsync`'i Polly'ye
geçirdiği bir lambda içinde çağırıyor:

```csharp
var result = await pipeline.ExecuteAsync(
    async token => await ChargeOnceAsync(request, attempts, token),
    cancellationToken);
```

FlowLens bunu **yakaladı**, çünkü metot bildiriminin tüm alt ağacını geziyor ve lambda gövdeleri
de o ağaçta. Sonuç doğru, **mekanizma yaklaşık**: Polly'nin delegate'i çağırdığı bilinmiyor,
çağrı metnin içinde görüldüğü için ekleniyor.

İki yan etkisi:
- Yaratılıp **hiç çağrılmayan** bir lambda da kenar üretir (yanlış pozitif).
- Delegate başka metoda geçirilip **orada** çağrılırsa kenar yanlış çağırana bağlanır.

Bu vakada ikisi de zararsız. Genel çözümü data-flow analizi gerektirir — roadmap §3'te kapsam
dışı.

---

## L13 — Kolon seviyesi eşlemesinin altı sınırı

**Durum:** Açık, hepsi ölçüldü ve raporlanıyor. Bir kısmı yapısal.
**Keşfedildiği yer:** Faz 3.

Kolon yazmaları `AssignmentExpressionSyntax` / `++` / `SetProperty` üzerinden bulunuyor ve EF Core
`IModel`'inden kolona bağlanıyor. Görünmeyenler:

| # | Sınır | Tip | Ne oluyor |
|---|---|---|---|
| 1 | `Ignore` edilmiş property'ler | Yapısal (doğru davranış) | `Order.TotalAmount`, `Order.Currency`, `DomainEvents` kolonu yok. Sessizce düşmüyor: *"property written but not mapped to a column"* diagnostic'i basılıyor |
| 2 | Shadow property'ler | Yapısal | `xmin`, `order_id`, owned koleksiyonların sentetik `id`'si — kaynakta C# üyesi yok, dolayısıyla `filePath`/`line` de yok. Column node **üretilmiyor**; uydurma konum yazmak zorunlu-alan invariant'ını kırardı |
| 3 | Change-tracker yazması | Kısmen kapatıldı | Materialize + mutasyon + `SaveChanges`, hiç EF mutation çağrısı yok. `SaveChangesWithEntityParameter` (ikinci sınıf) bunu entity düzeyinde yakalıyor, kolon düzeyinde değil |
| 4 | Raw SQL kolonları | Yapısal → L6 | `NaiveReservationStrategy.cs:37`'nin `UPDATE stock_items`'ı ve `ProductVectorRepository`'nin tüm erişimi. SQL string'i parse etmek roadmap'te yasak; siteler diagnostic olarak listeleniyor |
| 5 | `ToJson()` owned koleksiyon | Yapısal | `cart.carts.Items` tek bir `jsonb` kolonu; `CartItemRecord.ProductId/Quantity/AddedAtUtc`'nin kolonu **yok**, JSON yolu var. Diagnostic basılıyor |
| 6 | Paylaşılan value object'in kendi ataması | Yapısal | `Money.Amount = x` `Money`'nin ctor'unda; aynı üye `catalog.products.price_amount`, `payment.payments.Amount` **ve** `ordering.order_lines.UnitPrice`'ı besliyor. Üçünü birden iddia etmek tek yazmayı üç yazma göstermek olurdu. Sahibinin sitesinde (`product.Price = money`) tekil olarak çözülüyor ve orada kaydediliyor |

5 ve 6, tablo düzeyinde **kayıp değil** — ilgili tablolar başka sinyallerle graph'ta.

> **L2 güncellemesi:** L2'nin `AccessorDeclarationSyntax` endişesi Faz 3'te **konusuz kaldı.**
> `DbSet` erişimi ifadenin *tipinden* çözülüyor, accessor'ın şeklinden değil — dolayısıyla
> `DbSet<Order> Orders => Set<Order>()` ile `DbSet<Order> Orders { get; set; }` birebir aynı
> analiz ediliyor. Constructor'lar ise L9 kapsamında (dar biçimde) ele alındı.

---

## L14 — FlowLens hedefin EF Core sürümüne bağımlı

**Durum:** Kalıcı, bilinçli sınır. Aracın **genel amaçlı olmadığının** bir başka kanıtı.
**Keşfedildiği yer:** Faz 3 tasarımı; Faz 3 revizyonunda zorlayıcı hâle getirildi.

Tablo/kolon adları EF Core'un `IModel`'inden okunuyor (roadmap Faz 3 bunu şart koşuyor: isim
tahmini ve SQL parse yasak). Bu da hedefin **derlenmiş** DbContext'lerini **bu sürece** yüklemeyi
gerektiriyor.

`DbContext` ve `IModel` tiplerinin yükleme sınırının iki yanında **aynı `Type`** olması zorunlu —
aksi halde her cast patlar. Bu yüzden `TargetModelLoadContext`, `Microsoft.EntityFrameworkCore*` ve
`Npgsql*` adlarını Default context'e bırakıyor; yani **FlowLens'in kendi paket sürümleri** kazanıyor.

**Bağımlılık sürüm bazında sert:** .NET'in TPA listesi assembly'leri basit isimle eşleştirir ve
sürümü **umursamaz**. FlowLens'in EF'i hedefinkinden eski olamaz ve major'ı farklı olamaz — olursa
sessizce bağlanır, sonra model kurulurken alakasız bir noktada `MissingMethodException` olarak
patlar.

`EfPreflight` bunu **build'i durdurarak** uyguluyor: sürüm uyuşmazlığında hiçbir şey yüklenmez,
`graph.json` yazılmaz, exit 6. Sessiz bozulma yok. Kaçış bayrağı (`--allow-missing-model` gibi)
**bilerek eklenmedi** — bozuk çıktıyı etiketleyip kabul etmek, bu projenin var oluş sebebinin tersi.

### Farklı sürümlü bir kod tabanı için: EfProbe ayrı process'e taşınır

EF Core'a dokunan **tek** sınıf `EfProbe` (`EfProbeArchitectureTests` bunu zorluyor). Taşıma:

1. `EfProbe.cs` + `TargetModelLoadContext` + `EfVersionGate` yeni bir `FlowLens.EfProbe` exe'sine taşınır.
2. Exe'nin `Main`'i: `EfProbe.Read(...)` → `EfModelContract.Serialize(...)` → stdout.
3. `FlowLens.Core`: `Process.Start` + `EfModelContract.Deserialize(stdout)`.
4. EF/Npgsql paket referansları Core'dan o projeye taşınır.

3. adım **bugün de çalışıyor**: sınırı yalnız `EfModelSnapshot` geçiyor ve o tamamen string/bool.
`EfProbeContractTests` gerçek hedefin snapshot'ını her koşuda round-trip ediyor, yani taşınabilirlik
bir niyet değil **her koşuda doğrulanan bir olgu**.

Bugün yapılmadı çünkü tek bir hedef repo var ve süreç sınırı bedavaya gelmiyor (bir
`Process.Start`, bir JSON kontratı, ayrı bir build çıktısı). Maliyet ancak ikinci bir hedef
belirdiğinde haklı çıkar.

---

## L15 — SaveChanges interceptor'ının hangi context'e bağlı olduğu modelden çıkarılıyor

**Durum:** Açık, bilinçli varsayım. **Tek yönlü aşırı-yaklaşım.**
**Keşfedildiği yer:** Faz 3 `graph.json` denetimi (§5.8).

`ordering.outbox_messages` satırını `DomainEventToOutboxInterceptor` **SaveChanges sırasında**
yazıyor; hiçbir handler ondan bahsetmiyor. Bu yüzden "checkout hangi tablolara yazıyor?" sorusunun
cevabında outbox eksik kalıyordu. Kural eklendi (`mechanism: SaveChangesInterceptor`), ama iki
kaveatı var:

**1. Bağ DI'dan değil modelden kuruluyor.** Bir interceptor'ın hangi `DbContext`'e eklendiği
`options.AddInterceptors(...)` çağrısında yazıyor — generic bir yardımcının (`AddModuleDbContext<T>`)
içinde. L11 bu tür konfigürasyon okumasını yapısal olarak güvenilmez kaydediyor. Onun yerine:
interceptor'ın yazdığı entity'yi **tam olarak bir context eşliyorsa** o context'e bağlı sayılıyor.
İki context aynı entity'yi eşlerse hiçbir şey iddia edilmiyor.

> Bu, bir interceptor kayıtlı olmadığı bir context'e bağlıymış gibi görünebilir demek —
> ModularCommerce'te böyle bir durum yok (her outbox entity'si kendi modülünün context'inde) ama
> başka bir kod tabanında olabilir.

**2. Aşırı-yaklaşım.** Interceptor yalnız eşlenecek domain event varsa satır yazar; event
üretmeyen bir `SaveChanges` outbox'a dokunmaz. FlowLens ayrım yapmıyor, hepsini yazma sayıyor.
Impact analizi için doğru yanlılık bu — *yazılabilecek* bir tablo cevapta görünmeli — ama precision
düşürüyor.

**Faz 5 için:** eval set'i bu mekanizmayı ayrı ölçmeli; `SaveChangesWithEntityParameter` ve
`EntityConstruction` gibi işaretli ama bunlardan farklı olarak **kaçınılmaz** değil — DI okuması
eklenirse kesinleşebilir.

---

## L6 — Statik analizin yapısal olarak göremedikleri

**Durum:** Kalıcı sınır. Faz 5 eval setinde kategori olarak ölçülecek.

- **Reflection** — `Activator.CreateInstance`, `Type.GetMethod().Invoke`
- **Dynamic dispatch** — `dynamic` anahtar kelimesi
- **String tabanlı SQL** — `ProductVectorRepository` raw SQL kullanıyor (survey §7.1); dokunduğu
  kolonlar Roslyn'den çıkmaz
- **Config'e bağlı dallanma** — → L3
- **Kaynak üreteçleri** — üretilen kod syntax ağacında var ama `filePath` anlamlı değil

Bunlar çözülecek problemler değil, **ölçülecek** kayıplardır. Faz 5b'nin recall metriği tam
olarak bu kategorileri sayacak.
