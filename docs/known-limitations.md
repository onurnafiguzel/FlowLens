# FlowLens — Bilinen Sınırlamalar

> Bu dosya, FlowLens'in **bilerek yakalamadığı** veya **yapısal olarak yakalayamadığı** şeyleri
> kaydeder. Amaç dürüstlük: eksik bir kenar sessizce kaybolursa, impact analizi "bu akış
> hiçbir tabloya dokunmuyor" gibi güvenle yanlış bir cevap üretir. Yanlış cevap, cevapsızlıktan
> tehlikelidir.
>
> Her madde şunu taşır: **ne kaçırılıyor**, **neden**, **hangi fazda ele alınacak**.

---

## L1 — Minimal API endpoint'lerinin tamamı lambda; `MethodDeclarationSyntax` bunları yakalamıyor

**Durum:** Açık. Faz 2'de ele alınacak.
**Şu an etkisi:** Faz 1 metot sayımı endpoint'leri içermiyor.
**Faz 2 için etkisi:** Bu, call graph'ın **giriş noktalarının tamamı** demek. Çözülmeden Faz 2 başlayamaz.

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

### Doğrulama

Faz 2 bittiğinde şu doğru olmalı: `Endpoint` tipli node sayısı = 24 (Shipping hariç 8 modül),
ve `POST /api/ordering/checkout` node'undan `CheckoutHandler.HandleAsync`'e bir `CALLS` kenarı var.

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

**Durum:** Açık. Faz 2'de `SymbolFinder` ile kısmen çözülecek; tamamen çözülemez.

Faz 1'in SemanticModel demo'su bunu ölçtü: `CheckoutHandler.HandleAsync` içindeki **49
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

**Durum:** Açık. Faz 2'de registry okuma stratejisiyle ele alınacak.

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
