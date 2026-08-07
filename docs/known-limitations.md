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

## L5 — Design-time DbContext factory yok

**Durum:** Açık. Faz 3'te ele alınacak.

Roadmap Faz 3 "DbContext'i design-time factory ile örnekle" diyor. ModularCommerce'te
`IDesignTimeDbContextFactory` implementasyonu **yok** (survey §5).

Plan: `new DbContextOptionsBuilder<T>().UseNpgsql(sahte-connection-string)` ile elle kur.
`UseNpgsql` yalnızca provider seçer, bağlantı açmaz — `IModel` kurmak için veritabanı gerekmez.

**Discovery istisnası:** `DiscoveryModule` pgvector için gerçek bir `NpgsqlDataSource` kuruyor
ve veri kaynağı ilk bağlantıda tip kataloğunu cache'liyor. Ayrıca `discovery.product_embeddings`
tablosunun `vector(1536)` kolonu migration içinde **raw SQL** ile eklendiği için `IModel`'de
zaten görünmeyecek. Discovery'nin kolon seviyesi eşlemesi Faz 3'te eksik kalacak — bu bilinçli.

---

## L7 — Map fiili olmayan endpoint kayıtları bulunmuyor

**Durum:** Açık. Gerektiğinde ele alınacak.
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

## L8 — Graph'ta Shared.Kernel gürültüsü

**Durum:** Bilinçli kabul. Faz 3'te yeniden değerlendirilecek.

Checkout trace'inin 106 node'unun 89'u `Method` tipinde ve önemli bir kısmı
`Result.Success` / `Result.Failure` / `Error.Validation` gibi Shared.Kernel yardımcıları.
Bunlar **gerçek çağrılar** — uydurma değil — ama impact analizi açısından bilgi taşımıyorlar.

Faz 2'de filtrelenmedi çünkü "hangi çağrı önemsiz" kararı sezgisel ve roadmap'in
"eksik, fazladan tehlikelidir" ilkesine ters. Faz 3'te `Forward()` traversal'ı tablo/kolon
hedefine yürüdüğünde bu node'lar doğal olarak yaprak kalacak ve cevaba karışmayacak.

Framework/NuGet metotları zaten graph'a **girmiyor** (`SourceLocation.IsInSource` filtresi) —
`string.Join`, LINQ `Select`, `IValidator.ValidateAsync` gibi çağrılar dışarıda.

---

## L9 — Constructor çağrıları kenar üretmiyor

**Durum:** Açık. **DÜZELTİLEBİLİR** (yapısal sınır değil). Faz 3'te karara bağlanacak.
**Keşfedildiği yer:** Faz 2 doğrulaması, §5.1.

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
