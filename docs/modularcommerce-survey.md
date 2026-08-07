# ModularCommerce — Keşif Raporu (FlowLens hedef repo analizi)

> **Kapsam:** `C:\Users\USER\source\repos\ModularCommerce` read-only incelendi. Hiçbir dosya değiştirilmedi.
> **Tarih:** 2026-08-07
> **Amaç:** FlowLens Faz 1–3 için Roslyn extraction stratejisini tahmine değil, ölçüme dayandırmak.
>
> Bu dokümandaki **tüm dosya yolları ModularCommerce repo köküne göre relatiftir.** Satır numaraları
> inceleme anındaki `master` durumuna aittir.

---

## 0. Özet — FlowLens için en kritik 6 bulgu

| # | Bulgu | FlowLens'e etkisi |
|---|---|---|
| 1 | **MediatR YOK.** Handler'lar düz POCO sınıf, `HandleAsync` metodu, DI ile doğrudan endpoint lambda'sına inject ediliyor. | `IRequestHandler<,>` tabanlı hiçbir keşif çalışmaz. Endpoint→Handler kenarı, minimal API lambda'sının **parametre tiplerinden** çıkarılmalı. |
| 2 | **Controller YOK.** %100 minimal API, `IModule.MapEndpoints` içinden `MapGroup` + `MapPost/MapGet/...`. | Endpoint node'ları `[HttpPost]` attribute'undan değil, `MapX("...")` invocation'larından toplanacak. Route iki parçalı: modül prefix'i + endpoint suffix'i. |
| 3 | **Modül başına ayrı DbContext** — 8 adet, her biri kendi PostgreSQL şemasında. Shipping'in DbContext'i yok. | Faz 3'te tek `IModel` değil, **8 ayrı `IModel`** gezilecek. |
| 4 | **Design-time factory YOK.** DbContext'ler `AddModuleDbContext` ile connection string'e bağlı kayıtlı. | Faz 3'ün "design-time factory ile örnekle" varsayımı geçerli değil — FlowLens kendi `DbContextOptionsBuilder`'ını kurmalı (aşağıda §5'te detay). |
| 5 | **Repository + Queries ikilisi (CQRS-lite).** Yazma tarafı `I<X>Repository`, okuma tarafı `I<X>Queries`. İkisi de Infrastructure'da, DbContext'i doğrudan kullanıyor. Application katmanı EF Core'a **asla** dokunmuyor (architecture test'le zorlanıyor). | `WRITES`/`READS` kenarları Application'da değil, **Infrastructure'daki Repository/Queries implementasyonlarında** aranmalı. |
| 6 | **`Publish<T>()` generic çağrısı YOK.** Her iki outbox dispatcher da tip-silinmiş `publisher.Publish(object, Type)` kullanıyor; CLR tipi bir **registry dictionary'sinden** runtime'da çözülüyor. | Faz 2'deki "`Publish<T>()` görünce generic type argument'ı yakala" adımı bu repoda **hiçbir şey bulamaz.** Alternatif strateji §6.3'te. |

---

## 1. Solution ve proje yapısı

**Solution:** `ModularCommerce.sln` (repo kökü). `.slnx` yok.

**Toplam 66 `.csproj`** — 48'i `src/`, 18'i `tests/` altında.

> Bu sayı Faz 1'de FlowLens'in kendi çıktısıyla doğrulandı (`Loaded 66/66 projects`).
> Dağılım: 9 modül × 5 katman = 45, Host 1, Shared 2 → 48 src; 18 test projesi.

### 1.1 Modüller — 9 adet

`src/Modules/<Module>/` altında, her biri **5 katmanlı proje**:

| # | Modül | Şema | DbContext | Endpoint | Durum |
|---|---|---|---|---|---|
| 1 | Identity | `identity` | ✔ | 4 | Aktif |
| 2 | Catalog | `catalog` | ✔ | 4 | Aktif (kendi outbox'ı var) |
| 3 | Cart | `cart` | ✔ | 4 | Aktif (Postgres + Redis cache) |
| 4 | Inventory | `inventory` | ✔ | 5 | Aktif |
| 5 | Ordering | `ordering` | ✔ | 4 | Aktif (outbox var) |
| 6 | Payment | `payment` | ✔ | 1 (dev) | Aktif |
| 7 | Shipping | — | ✘ | 0 | **Boş kabuk** |
| 8 | Notification | `notification` | ✔ | 1 (dev) | Aktif (consumer) |
| 9 | Discovery | `discovery` | ✔ | 1 | Aktif (consumer, pgvector) |

Modül listesi kaynağı: [`src/Bootstrapper/ModularCommerce.Host/Program.cs:40-51`](../../ModularCommerce/src/Bootstrapper/ModularCommerce.Host/Program.cs) — statik `IModule[]` dizisi.

### 1.2 Katman deseni (modül başına)

```
src/Modules/<M>/ModularCommerce.<M>.Domain           → sadece Shared.Kernel'e bakar, EF Core YASAK
src/Modules/<M>/ModularCommerce.<M>.Application      → Domain + Contracts
src/Modules/<M>/ModularCommerce.<M>.Infrastructure   → Application
src/Modules/<M>/ModularCommerce.<M>.Api              → Infrastructure + Shared.Infrastructure
src/Modules/<M>/ModularCommerce.<M>.Contracts        → sadece Shared.Kernel (modüller arası TEK kapı)
```

Referans yönü: `Api → Infrastructure → Application → {Domain, Contracts}`, `Domain → Shared.Kernel`.

### 1.3 Modül dışı projeler

| Proje | Yol |
|---|---|
| Host (composition root) | `src/Bootstrapper/ModularCommerce.Host/` |
| Shared.Kernel (`Result`, `Error`, `Entity`, `IDomainEvent`) | `src/Shared/ModularCommerce.Shared.Kernel/` |
| Shared.Infrastructure (DbContext ext, EventBus, Auth, RateLimiting, Health) | `src/Shared/ModularCommerce.Shared.Infrastructure/` |

**Test projeleri:** 21 adet — `ModularCommerce.ArchitectureTests`, modül başına `*.UnitTests` / `*.IntegrationTests`, ve `ModularCommerce.TestKit`.

> **FlowLens notu:** `MSBuildWorkspace` ile solution yüklerken **66 proje** açılacak. Faz 1'de test projelerini filtrelemek isteyebilirsin (`tests/` prefix'i), aksi halde node sayısı gereksiz şişer.

### 1.4 Build ortamı

- `global.json` → SDK `10.0.100`, `rollForward: latestFeature`
- `Directory.Build.props` → `net10.0`, `LangVersion=latest`, nullable + implicit usings, **`TreatWarningsAsErrors=true`**
- `Directory.Packages.props` → central package management; csproj'larda versiyon yok

---

## 2. Endpoint'ler — %100 Minimal API

**Controller YOK.** `ControllerBase`, `[ApiController]`, `[HttpGet]` benzeri hiçbir MVC yapısı bulunamadı. Aramada çıkan 31 eşleşmenin tamamı `MapGroup`/`MapPost`/`MapGet` idi.

### 2.1 İki kademeli route yapısı

**Kademe 1 — modül prefix'i**, `<Module>Module.MapEndpoints` içinde:

| Prefix | Dosya:satır |
|---|---|
| `/api/identity` | [`src/Modules/Identity/ModularCommerce.Identity.Api/IdentityModule.cs:45`](../../ModularCommerce/src/Modules/Identity/ModularCommerce.Identity.Api/IdentityModule.cs) |
| `/api/catalog` | `src/Modules/Catalog/ModularCommerce.Catalog.Api/CatalogModule.cs:75` |
| `/api/cart` | `src/Modules/Cart/ModularCommerce.Cart.Api/CartModule.cs:44` |
| `/api/inventory` | `src/Modules/Inventory/ModularCommerce.Inventory.Api/InventoryModule.cs:77` |
| `/api/ordering` | `src/Modules/Ordering/ModularCommerce.Ordering.Api/OrderingModule.cs:59` |
| `/api/payment` | `src/Modules/Payment/ModularCommerce.Payment.Api/PaymentModule.cs:77` |
| `/api/notification` | `src/Modules/Notification/ModularCommerce.Notification.Api/NotificationModule.cs:49` |
| `/api/discovery` | `src/Modules/Discovery/ModularCommerce.Discovery.Api/DiscoveryModule.cs:117` |
| — (yok) | Shipping: `ShippingModule.MapEndpoints` boş, `src/Modules/Shipping/ModularCommerce.Shipping.Api/ShippingModule.cs:19-23` |

**Kademe 2 — endpoint suffix'i**, `<X>Endpoints` static extension sınıflarında (`Api/Endpoints/` klasörü).

### 2.2 Endpoint envanteri (24 adet)

| Metot + route | Dosya:satır |
|---|---|
| `POST /api/identity/signup` | `src/Modules/Identity/.../Endpoints/AuthEndpoints.cs:20` |
| `POST /api/identity/login` | `AuthEndpoints.cs:33` |
| `POST /api/identity/refresh` | `AuthEndpoints.cs:42` |
| `POST /api/identity/logout` | `AuthEndpoints.cs:52` |
| `GET /api/catalog/products` | `src/Modules/Catalog/.../Endpoints/ProductEndpoints.cs:24` |
| `GET /api/catalog/products/{id:guid}` | `ProductEndpoints.cs:33` |
| `POST /api/catalog/products` | `ProductEndpoints.cs:43` |
| `PUT /api/catalog/products/{id:guid}` | `ProductEndpoints.cs:54` |
| `GET /api/cart` | `src/Modules/Cart/.../Endpoints/CartEndpoints.cs:25` |
| `POST /api/cart/items` | `CartEndpoints.cs:34` |
| `PUT /api/cart/items/{productId:guid}` | `CartEndpoints.cs:45` |
| `DELETE /api/cart/items/{productId:guid}` | `CartEndpoints.cs:57` |
| `POST /api/inventory/reservations` | `src/Modules/Inventory/.../Endpoints/ReservationEndpoints.cs:14` |
| `GET /api/inventory/reservations/{id:guid}` | `ReservationEndpoints.cs:26` |
| `GET /api/inventory/stock/{productId:guid}` | `src/Modules/Inventory/.../Endpoints/StockEndpoints.cs:17` |
| `PUT /api/inventory/dev/stock/{productId:guid}` | `StockEndpoints.cs:32` |
| `POST /api/inventory/dev/reservations/{id:guid}/expire-now` | `StockEndpoints.cs:46` |
| `POST /api/ordering/checkout` | `src/Modules/Ordering/.../Endpoints/OrderEndpoints.cs:22` |
| `GET /api/ordering/orders/{id:guid}` | `OrderEndpoints.cs:42` |
| `GET /api/ordering/orders` | `OrderEndpoints.cs:52` |
| `POST /api/ordering/orders/{id:guid}/cancel` | `OrderEndpoints.cs:61` |
| `GET /api/payment/dev/payments` | `src/Modules/Payment/.../Endpoints/PaymentDevEndpoints.cs:22` |
| `GET /api/notification/dev/logs/{orderId:guid}` | `src/Modules/Notification/.../Endpoints/NotificationDevEndpoints.cs:18` |
| `POST /api/discovery/search` | `src/Modules/Discovery/.../Endpoints/SearchEndpoints.cs:17` |

Ayrıca modül dışı: `GET /` (`Program.cs:79`), `/health/live` + `/health/ready` (`Program.cs:77` → `MapHealthEndpoints`).

### 2.3 ⚠️ Route birleştirmede iki tuzak

**Tuzak 1 — üçüncü, gizli bir `MapGroup` katmanı.** Ordering ve Cart, auth için boş prefix'li bir ara grup açıyor:

```csharp
// src/Modules/Ordering/ModularCommerce.Ordering.Api/Endpoints/OrderEndpoints.cs:19-20
var secured = ((RouteGroupBuilder)group).MapGroup("")
    .RequireAuthorization();
```

Aynısı `CartEndpoints.cs:22`'de. FlowLens `MapGroup("")` çağrısını route'a `""` ekleyerek atlamalı — aksi halde `/api/ordering//checkout` üretir.

**Tuzak 2 — prefix ve suffix farklı dosyalarda.** `MapPost("/checkout", ...)` çağrısı `OrderEndpoints.cs`'te, `/api/ordering` prefix'i `OrderingModule.cs`'te. Tam route'u kurmak için `MapOrderEndpoints` extension metodunun **çağrıldığı yeri** semantic model ile bulmak gerekiyor. Bu bir zincir:

```
Program.cs:87  module.MapEndpoints(app)
  → OrderingModule.cs:59  endpoints.MapGroup("/api/ordering")
  → OrderingModule.cs:61  group.MapOrderEndpoints()
  → OrderEndpoints.cs:19  MapGroup("")
  → OrderEndpoints.cs:22  MapPost("/checkout", ...)
```

Beş adımlık bu zinciri Faz 2'de çözmek gerekecek. **Basitleştirme önerisi (Faz 2 MVP):** prefix'i `<Module>Module.MapEndpoints` içindeki tek `MapGroup` literalinden, suffix'i `<Module>.Api/Endpoints/*.cs` içindeki `MapX` literallerinden al, modül adıyla eşleştir. Desen 8 modülün 8'inde de aynı olduğu için bu yeterli; genel çözüm sonraya bırakılabilir.

### 2.4 Endpoint → Handler kenarı nasıl çıkarılır

Handler, lambda'nın **parametresi** olarak DI'dan geliyor:

```csharp
// OrderEndpoints.cs:22-29
secured.MapPost("/checkout", async (
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    ClaimsPrincipal user,
    CheckoutHandler handler,              // ← Handler node'u BU parametrenin tipi
    CancellationToken cancellationToken) =>
{
    var command = new CheckoutCommand(user.GetUserId(), idempotencyKey ?? string.Empty);
    var result = await handler.HandleAsync(command, cancellationToken);   // ← CALLS kenarı
```

**Kural:** `MapX` çağrısının lambda argümanının parametrelerini gez, `ClaimsPrincipal` / `CancellationToken` / `HttpContext` / `[FromBody]`-`[FromHeader]` işaretli olanları ele, kalan sınıf tipi handler'dır. Doğrulama: gövdedeki `handler.HandleAsync(...)` invocation'ı zaten `CALLS` kenarını verir — yani lambda parametresini özel-durum yapmadan, sadece invocation'ları takip ederek de aynı sonuca ulaşılır. **Bu ikinci yol daha sağlam.**

---

## 3. MediatR — kullanılmıyor

**Bulgu: MediatR YOK.**

- `Directory.Packages.props` içinde MediatR paketi yok (tüm paket listesi kontrol edildi).
- `MediatR|IRequestHandler|IRequest<|ISender|IMediator` araması tüm repoda **tek dosyada** eşleşti: `docs/hafta-6-notlar.md` — bu bir kod dosyası değil, MediatR'ın neden kullanılmadığını anlatan tasarım notu.

### 3.1 Yerine ne var — düz handler sınıfları

Handler'lar interface implement etmeyen `sealed class`'lar; primary constructor ile bağımlılık alıyor, `HandleAsync` metodu `Result<T>` dönüyor:

```csharp
// src/Modules/Catalog/.../Products/GetProducts/GetProductsHandler.cs:9-15
public sealed class GetProductsHandler(
    IProductQueries queries,
    IValidator<GetProductsQuery> validator)
{
    public async Task<Result<PagedResponse<ProductSummaryResponse>>> HandleAsync(
        GetProductsQuery query,
        CancellationToken cancellationToken)
```

DI kaydı elle, modülün `Register`'ında:

```csharp
// src/Modules/Ordering/ModularCommerce.Ordering.Api/OrderingModule.cs:50-53
services.AddScoped<CheckoutHandler>();
services.AddScoped<CancelOrderHandler>();
services.AddScoped<GetOrderHandler>();
services.AddScoped<GetMyOrdersHandler>();
```

### 3.2 Namespace / klasör pattern'i

Baskın desen (22 handler'ın 20'si):

```
ModularCommerce.<Module>.Application.<Aggregate>.<UseCase>.<UseCase>Handler
```

Dosya yolu ile birebir örtüşüyor: `<Module>.Application/<Aggregate>/<UseCase>/<UseCase>Handler.cs`

**Tam handler envanteri (22 adet, namespace'ler doğrulandı):**

| Handler | Namespace |
|---|---|
| `AddItemHandler` | `ModularCommerce.Cart.Application.Carts.AddItem` |
| `GetCartHandler` | `ModularCommerce.Cart.Application.Carts.GetCart` |
| `RemoveItemHandler` | `ModularCommerce.Cart.Application.Carts.RemoveItem` |
| `UpdateItemQuantityHandler` | `ModularCommerce.Cart.Application.Carts.UpdateItemQuantity` |
| `CreateProductHandler` | `ModularCommerce.Catalog.Application.Products.CreateProduct` |
| `GetProductByIdHandler` | `ModularCommerce.Catalog.Application.Products.GetProductById` |
| `GetProductsHandler` | `ModularCommerce.Catalog.Application.Products.GetProducts` |
| `UpdateProductHandler` | `ModularCommerce.Catalog.Application.Products.UpdateProduct` |
| `IndexProductHandler` | `ModularCommerce.Discovery.Application.Indexing` ⚠️ |
| `SearchProductsHandler` | `ModularCommerce.Discovery.Application.Search` ⚠️ |
| `LoginHandler` | `ModularCommerce.Identity.Application.Auth.Login` |
| `LogoutHandler` | `ModularCommerce.Identity.Application.Auth.Logout` |
| `RefreshHandler` | `ModularCommerce.Identity.Application.Auth.Refresh` |
| `SignupHandler` | `ModularCommerce.Identity.Application.Auth.Signup` |
| `GetReservationHandler` | `ModularCommerce.Inventory.Application.Reservations.GetReservation` |
| `ReserveStockHandler` | `ModularCommerce.Inventory.Application.Reservations.ReserveStock` |
| `GetStockHandler` | `ModularCommerce.Inventory.Application.Stock.GetStock` |
| `SetStockHandler` | `ModularCommerce.Inventory.Application.Stock.SetStock` |
| `CancelOrderHandler` | `ModularCommerce.Ordering.Application.Orders.Cancel` |
| `CheckoutHandler` | `ModularCommerce.Ordering.Application.Orders.Checkout` |
| `GetMyOrdersHandler` | `ModularCommerce.Ordering.Application.Orders.GetMyOrders` |
| `GetOrderHandler` | `ModularCommerce.Ordering.Application.Orders.GetOrder` |

⚠️ **İki istisna:** Discovery'nin iki handler'ı `<Aggregate>.<UseCase>` yerine tek segment kullanıyor (`.Indexing`, `.Search`). Namespace'e regex uydurma — **`*Handler` sonekiyle + `.Application` namespace'i içinde olma** kriteri yeterli ve daha sağlam.

`GlobalExceptionHandler` (`src/Shared/ModularCommerce.Shared.Infrastructure/ExceptionHandling/`) isim olarak eşleşiyor ama use-case handler'ı **değil** — `IExceptionHandler` implementasyonu. Filtrelenmeli.

---

## 4. EF Core DbContext'ler

**Modül başına ayrı DbContext. Toplam 8 adet.** Shipping'in DbContext'i yok.

| DbContext | Şema | Dosya:satır |
|---|---|---|
| `CartDbContext` | `cart` | `src/Modules/Cart/ModularCommerce.Cart.Infrastructure/Persistence/CartDbContext.cs:8` |
| `CatalogDbContext` | `catalog` | `src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Persistence/CatalogDbContext.cs:10` |
| `DiscoveryDbContext` | `discovery` | `src/Modules/Discovery/ModularCommerce.Discovery.Infrastructure/Persistence/DiscoveryDbContext.cs:15` |
| `IdentityDbContext` | `identity` | `src/Modules/Identity/ModularCommerce.Identity.Infrastructure/Persistence/IdentityDbContext.cs:9` |
| `InventoryDbContext` | `inventory` | `src/Modules/Inventory/ModularCommerce.Inventory.Infrastructure/Persistence/InventoryDbContext.cs:9` |
| `NotificationDbContext` | `notification` | `src/Modules/Notification/ModularCommerce.Notification.Infrastructure/Persistence/NotificationDbContext.cs:10` |
| `OrderingDbContext` | `ordering` | `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Persistence/OrderingDbContext.cs:10` |
| `PaymentDbContext` | `payment` | `src/Modules/Payment/ModularCommerce.Payment.Infrastructure/Persistence/PaymentDbContext.cs:9` |

Hepsi `<Module>.Infrastructure/Persistence/` altında, `sealed`, aynı kalıpta:

```csharp
// src/Modules/Ordering/.../Persistence/OrderingDbContext.cs:7-18
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options)
    : DbContext(options)
{
    public const string Schema = "ordering";
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);
    }
}
```

### 4.1 Kayıt noktası — tek ortak extension

Hepsi `AddModuleDbContext<T>` üzerinden kaydediliyor ([`src/Shared/ModularCommerce.Shared.Infrastructure/Persistence/ModuleDbContextExtensions.cs:9-36`](../../ModularCommerce/src/Shared/ModularCommerce.Shared.Infrastructure/Persistence/ModuleDbContextExtensions.cs)):

```csharp
public static IServiceCollection AddModuleDbContext<TContext>(
    this IServiceCollection services,
    IConfiguration configuration,
    string schema,
    Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
    where TContext : DbContext
{
    var connectionString = configuration.GetConnectionString("Database") ?? throw ...;
    services.AddDbContext<TContext>((serviceProvider, options) =>
    {
        options.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema));
        configure?.Invoke(serviceProvider, options);
    });
    services.AddHostedService<MigrateAndSeedHostedService<TContext>>();
    return services;
}
```

8 çağrı yeri:

| Modül | Dosya:satır | `configure` hook'u |
|---|---|---|
| Cart | `CartModule.cs:26` | yok |
| Catalog | `CatalogModule.cs:37` | ✔ (outbox interceptor) |
| Discovery | `DiscoveryModule.cs:38` | ✔ (pgvector `NpgsqlDataSource`) |
| Identity | `IdentityModule.cs:28` | yok |
| Inventory | `InventoryModule.cs:35` | yok |
| Notification | `NotificationModule.cs:26` | yok |
| Ordering | `OrderingModule.cs:38` | ✔ (outbox interceptor) |
| Payment | `PaymentModule.cs:29` | yok |

### 4.2 Migration'lar

`<Module>.Infrastructure/Persistence/Migrations/` (Cart, Discovery, Notification'da bir seviye yukarıda: `Infrastructure/Migrations/`). Her modülün kendi `__EFMigrationsHistory` tablosu kendi şemasında.

Migration stratejisi `docs/hafta-2-notlar.md:9-12`'de: `dotnet ef` CLI ile üretilir; development'ta `MigrateAndSeedHostedService` uygulama trafik almadan migrate + seed yapar; production'da `dotnet ef database update` açık komut olarak koşulur.

---

## 5. Design-time DbContext factory — YOK

**Bulgu: `IDesignTimeDbContextFactory` implementasyonu bulunamadı.** Tüm repoda (`src` + `tests` + `docs`) `IDesignTimeDbContextFactory` ve `DesignTime` aramaları **sıfır sonuç** verdi.

Migration'lar muhtemelen `dotnet ef` CLI'nin **Host'u startup-project olarak kullanan** varsayılan davranışıyla üretiliyor (`Program.cs`'ten host builder'ı bulup DI'dan DbContext alma yolu). `docs/` içinde açık `--startup-project` örneği bulamadım; sadece `dotnet ef migrations add` sonrası `--no-build` kullanılmaması gerektiğine dair tuzak notları var (`docs/hafta-3-notlar.md:69`, `docs/hafta-5-notlar.md:109`).

### 5.1 ⚠️ Bu, FlowLens Faz 3 için doğrudan bir plan revizyonu

Roadmap `Faz 3` şunu diyor:

> *"DbContext'i design-time factory ile örnekle, veritabanına bağlanma gerekmiyor."*

**Bu yol kapalı — factory yok.** Üç alternatif var:

| Seçenek | Nasıl | Değerlendirme |
|---|---|---|
| **A. FlowLens kendi options'ını kurar** | `new DbContextOptionsBuilder<OrderingDbContext>().UseNpgsql("Host=x;Database=y")` ile ctor'u çağır. `UseNpgsql` connection string'i **kullanmaz, sadece provider'ı seçer** — `IModel` kurmak için DB bağlantısı gerekmez. | **Önerilen.** Sahte connection string yeterli. Ama FlowLens'in ModularCommerce assembly'lerine **referans vermesi** gerekir (reflection ya da proje referansı). |
| **B. Assembly'leri reflection ile yükle** | Build çıktısındaki `ModularCommerce.*.Infrastructure.dll`'leri `Assembly.LoadFrom` ile aç, `DbContext` alt tiplerini bul, `Activator` ile örnekle. | Proje referansı gerekmez, ama .NET 10 assembly load context ve transitive bağımlılık dertleri çıkar. |
| **C. `IModel`'i hiç kullanma** | `IEntityTypeConfiguration` sınıflarındaki `ToTable("...")` / `HasColumnName("...")` çağrılarını **Roslyn ile** oku. | Roadmap'in "isim tahmin etme" kuralına uyar (literal okuyor, tahmin etmiyor) ama EF Core'un varsayılan isimlendirme kurallarını (explicit `ToTable` yoksa) kaçırır. |

**Ek engel — Discovery.** `DiscoveryModule.cs:38`'deki `configure` hook'u pgvector için gerçek bir `NpgsqlDataSource` kuruyor ve `docs`'a göre veri kaynağı **ilk bağlantıda tip kataloğunu cache'liyor.** Discovery'nin `IModel`'i DB olmadan kurulamayabilir. Faz 3'te Discovery'yi ayrı ele al veya kapsam dışı bırak; kalan 7 DbContext seçenek A ile sorunsuz olmalı. Ayrıca `discovery.product_embeddings`'in `vector(1536)` kolonu migration içinde **raw SQL** ile eklendiği için `IModel`'de zaten görünmeyecek.

---

## 6. MassTransit — contract'lar, publish, consume

### 6.1 Event contract'ları nerede duruyor

**Her modülün kendi `Contracts` projesinde, `IntegrationEvents/` klasöründe.** Merkezi bir "Contracts" projesi yok — bu, modül sınırı kuralının sonucu (bir modül sadece başkasının `Contracts`'ına bakabilir).

Toplam **4 integration event**:

| Event | Dosya | Sahibi | Tüketen |
|---|---|---|---|
| `OrderPaid` | `src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IntegrationEvents/OrderPaid.cs:2` | Ordering | Notification |
| `OrderCancelled` | `src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IntegrationEvents/OrderCancelled.cs` | Ordering | (henüz yok) |
| `ProductCreated` | `src/Modules/Catalog/ModularCommerce.Catalog.Contracts/IntegrationEvents/ProductCreated.cs:7` | Catalog | Discovery |
| `ProductUpdated` | `src/Modules/Catalog/ModularCommerce.Catalog.Contracts/IntegrationEvents/ProductUpdated.cs` | Catalog | Discovery |

Hepsi `sealed record`:

```csharp
// src/Modules/Ordering/ModularCommerce.Ordering.Contracts/IntegrationEvents/OrderPaid.cs:2-7
public sealed record OrderPaid(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    string Currency,
    DateTime OccurredOnUtc);
```

> **Kritik ayrım:** `ModularCommerce.Ordering.Contracts.IntegrationEvents.OrderPaid` (integration event, POCO) ile `ModularCommerce.Ordering.Domain.Orders.OrderPaid` (domain event) **aynı isimde, farklı tiplerdir.** FlowLens node id'lerinde tam nitelikli isim kullanmalı, yoksa bu ikisi çakışır. Registry'de `using` alias'larıyla ayrıştırılmışlar (`OrderingIntegrationEventRegistry.cs:2-3`).

### 6.2 Bus kaydı — tek noktada

MassTransit **bir kez**, Shared.Infrastructure'da kaydediliyor:

```csharp
// src/Shared/ModularCommerce.Shared.Infrastructure/Messaging/EventBusExtensions.cs:16-34
public static IServiceCollection AddEventBus(
    this IServiceCollection services,
    IConfiguration configuration,
    Action<IBusRegistrationConfigurator>? configureConsumers = null)
{
    ...
    services.AddMassTransit(x =>
    {
        x.SetKebabCaseEndpointNameFormatter();
        configureConsumers?.Invoke(x);
        x.UsingRabbitMq((context, cfg) => { cfg.Host(new Uri(connectionString)); cfg.ConfigureEndpoints(context); });
    });
```

Consumer'lar composition root'tan enjekte ediliyor:

```csharp
// src/Bootstrapper/ModularCommerce.Host/Program.cs:57-61
builder.Services.AddEventBus(builder.Configuration, consumers =>
{
    consumers.AddConsumer<OrderPaidNotificationConsumer, OrderPaidNotificationConsumerDefinition>();
    consumers.AddConsumer<ProductChangedConsumer, ProductChangedConsumerDefinition>();
});
```

> **FlowLens için:** `AddConsumer<TConsumer, TDefinition>()` çağrıları `Program.cs`'te toplu duruyor — hangi consumer'ların gerçekten aktif olduğunu bulmanın **en ucuz yolu bu iki satır.**

### 6.3 Publish örnekleri — 2 adet, ikisi de tip-silinmiş

**⚠️ Bu, FlowLens Faz 2 planı için ikinci revizyon noktası.** Roadmap "`Publish<T>()` görünce generic type argument'ı yakala" diyor. Bu repoda **generic `Publish<T>()` çağrısı yok.** Her iki publish de outbox dispatcher'ında ve `object` + `Type` alıyor:

**Örnek 1 — Ordering outbox dispatcher:**

```csharp
// src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Outbox/OutboxDispatcher.cs:86-100
var clrType = mapper.ResolveType(message.Type);       // ← tip runtime'da dictionary'den geliyor
if (clrType is null) { ... }
try
{
    var integrationEvent = JsonSerializer.Deserialize(message.Content, clrType, JsonOptions)!;

    // Tip-silinmiş publish: registry CLR tipini verir, MassTransit doğru
    // exchange'e yönlendirir (D6). Generic Publish<T> derleme anında bilinemez.
    await publisher.Publish(integrationEvent, clrType, cancellationToken);

    message.ProcessedOnUtc = DateTime.UtcNow;
```

**Örnek 2 — Catalog outbox dispatcher:** aynı desen, `src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Outbox/CatalogOutboxDispatcher.cs:98`.

**`IPublishEndpoint` çözümleme noktaları:** `OutboxDispatcher.cs:57` ve `CatalogOutboxDispatcher.cs:56` (scope'tan `GetRequiredService`), parametre olarak `OutboxDispatcher.cs:69` ve `CatalogOutboxDispatcher.cs:68`.

#### `PUBLISHES` kenarını nasıl çıkarmalı

Syntax'tan tip çıkmıyor. **Registry sınıfı ground truth:**

```csharp
// src/Modules/Ordering/.../Outbox/OrderingIntegrationEventRegistry.cs:19-38
private readonly Dictionary<Type, (string Type, Func<IDomainEvent, object> Factory)> _map = new()
{
    [typeof(DomainEvents.OrderPaid)] = (OrderPaidType, e => {
        var d = (DomainEvents.OrderPaid)e;
        return new ContractEvents.OrderPaid(d.OrderId, d.CustomerId, d.TotalAmount, d.Currency, d.OccurredOnUtc);
    }),
    [typeof(DomainEvents.OrderCancelled)] = (OrderCancelledType, e => { ... }),
};

private readonly Dictionary<string, Type> _types = new()
{
    [OrderPaidType]      = typeof(ContractEvents.OrderPaid),
    [OrderCancelledType] = typeof(ContractEvents.OrderCancelled),
};
```

**Önerilen strateji:** `IIntegrationEventMapper` implementasyonlarını bul, içlerindeki `typeof(...)` ifadelerini Roslyn ile topla. Sınıf yorumunda da yazdığı gibi bu registry "**TEK genişleme noktası (OCP)**" — yeni event yayınlamak = buraya bir satır. Yani registry'yi okumak, publish edilen event kümesini **eksiksiz** verir.

Registry'ler:
- `src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Outbox/OrderingIntegrationEventRegistry.cs`
- `src/Modules/Catalog/ModularCommerce.Catalog.Infrastructure/Outbox/CatalogIntegrationEventRegistry.cs`

Domain event → outbox satırı yazan interceptor: `DomainEventToOutboxInterceptor` (her iki modülde). Domain event'i **kim raise ediyor** sorusu `Entity.Raise(...)` çağrılarından çıkar (`Shared.Kernel`).

> **Not:** Testlerde generic `Publish` var (`tests/ModularCommerce.Ordering.IntegrationTests/OrderPaidPublishConsumeTests.cs:35`, `tests/ModularCommerce.Notification.IntegrationTests/OrderPaidConsumerHarnessTests.cs:61,80`) — `harness.Bus.Publish(new OrderPaid(...))`. Test projelerini tararsan bunlar sahte `PUBLISHES` kenarı üretir. Test projeleri filtrelenmeli.

### 6.4 `IConsumer<T>` örnekleri — 2 adet

**Örnek 1 — tek event tüketen consumer:**

```csharp
// src/Modules/Notification/ModularCommerce.Notification.Api/Consumers/OrderPaidNotificationConsumer.cs:7-24
public sealed class OrderPaidNotificationConsumer(INotificationProcessor processor)
    : IConsumer<OrderPaid>
{
    public async Task Consume(ConsumeContext<OrderPaid> context)
    {
        var message = context.Message;
        var instruction = new NotificationInstruction(
            IdempotencyKey: $"OrderPaid:{message.OrderId}",
            ConsumerType: nameof(OrderPaidNotificationConsumer),
            OrderId: message.OrderId, ...);
        var result = await processor.ProcessAsync(instruction, context.CancellationToken);
```

**Örnek 2 — ⚠️ İKİ event tüketen tek consumer:**

```csharp
// src/Modules/Discovery/ModularCommerce.Discovery.Api/Consumers/ProductChangedConsumer.cs:12-25
public sealed class ProductChangedConsumer(IndexProductHandler handler)
    : IConsumer<ProductCreated>, IConsumer<ProductUpdated>      // ← iki interface
{
    public Task Consume(ConsumeContext<ProductCreated> context)
    {
        var m = context.Message;
        return IndexAsync(new ProductIndexRequest(m.ProductId, m.Name, m.Description, m.Sku), context.CancellationToken);
    }

    public Task Consume(ConsumeContext<ProductUpdated> context) { ... }
}
```

FlowLens `CONSUMES` kenarını çıkarırken **bir sınıfın birden fazla `IConsumer<T>` implement edebileceğini** hesaba katmalı — tek kenar değil, base type listesindeki **her** `IConsumer<T>` için bir kenar.

Her ikisi de `<Module>.Api/Consumers/` altında; yanlarında retry politikasını taşıyan `*ConsumerDefinition` sınıfları var (`ProductChangedConsumerDefinition.cs:9`, `OrderPaidNotificationConsumerDefinition.cs:4`).

### 6.5 Modüller arası köprünün tam hali

Roadmap Faz 2'nin "modüller arası köprü" hedefi bu repoda **iki hoplu**:

```
Ordering.Domain: Order.MarkPaid() → Raise(Domain.OrderPaid)
  → DomainEventToOutboxInterceptor  (SaveChanges ile aynı transaction)
    → OrderingIntegrationEventRegistry.TryMap()  → Contracts.OrderPaid
      → ordering.outbox_messages satırı
        → OutboxDispatcher (BackgroundService, ~1sn poll) → publisher.Publish(obj, Type)
          → RabbitMQ
            → OrderPaidNotificationConsumer.Consume(ConsumeContext<OrderPaid>)
              → INotificationProcessor.ProcessAsync
```

Saf `SymbolFinder.FindImplementationsAsync` bu zinciri kuramaz — ortada JSON serialize + DB tablosu + BackgroundService var. **Köprüyü kuran şey event tipinin kendisi:** `Contracts.OrderPaid` tipini registry'de (publish tarafı) ve `IConsumer<T>` base type'ında (consume tarafı) eşleştir. Bu FlowLens'in `Event` node'unu doğal bir eklem noktası yapıyor — tam da roadmap'teki veri modelinin öngördüğü gibi.

---

## 7. Repository pattern

**Var — ama tek başına değil. Yazma/okuma ayrılmış (CQRS-lite).**

### 7.1 Yazma tarafı — Repository

Interface **Domain**'de, implementasyon **Infrastructure**'da:

| Interface (Domain) | Implementasyon (Infrastructure) |
|---|---|
| `src/Modules/Ordering/.../Domain/Orders/IOrderRepository.cs` | `src/Modules/Ordering/.../Infrastructure/Persistence/Repositories/OrderRepository.cs` |
| `src/Modules/Catalog/.../Domain/Products/IProductRepository.cs` | `src/Modules/Catalog/.../Infrastructure/Persistence/Repositories/ProductRepository.cs` |
| `src/Modules/Identity/.../Domain/Users/IUserRepository.cs` | `.../Infrastructure/Persistence/Repositories/UserRepository.cs` |
| `src/Modules/Identity/.../Domain/Users/IRefreshTokenRepository.cs` | `.../Infrastructure/Persistence/Repositories/RefreshTokenRepository.cs` |
| `src/Modules/Inventory/.../Domain/Stock/IStockItemRepository.cs` | `.../Infrastructure/Persistence/Repositories/StockItemRepository.cs` |
| `src/Modules/Cart/.../Domain/Carts/ICartRepository.cs` | `.../Infrastructure/Persistence/PostgresCartRepository.cs` **+** `CachingCartRepository.cs` (decorator) |
| `src/Modules/Discovery/.../Application/Abstractions/IProductVectorRepository.cs` | `.../Infrastructure/Persistence/ProductVectorRepository.cs` (raw SQL, pgvector) |

DbContext repository'nin içinde, primary constructor ile:

```csharp
// src/Modules/Ordering/.../Repositories/OrderRepository.cs:9-30
public sealed class OrderRepository(OrderingDbContext context) : IOrderRepository
{
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => context.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    ...
    public async Task<Result> AddAsync(Order order, CancellationToken cancellationToken)
    {
        context.Orders.Add(order);                              // ← WRITES kenarı
        try { await context.SaveChangesAsync(cancellationToken); ... }
```

### 7.2 Okuma tarafı — Queries (repository'yi baypas eder)

Interface **Application/Abstractions**'da, implementasyon Infrastructure'da:

| Interface | Implementasyon |
|---|---|
| `src/Modules/Ordering/.../Application/Abstractions/IOrderQueries.cs` | `.../Infrastructure/Persistence/Queries/OrderQueries.cs` |
| `src/Modules/Catalog/.../Application/Abstractions/IProductQueries.cs` | `.../Infrastructure/Persistence/Queries/ProductQueries.cs` **+** `Caching/CachingProductQueries.cs` (decorator) |
| `src/Modules/Inventory/.../Application/Abstractions/IInventoryQueries.cs` | `.../Infrastructure/Persistence/Queries/InventoryQueries.cs` |

```csharp
// src/Modules/Ordering/.../Queries/OrderQueries.cs:7-22
public sealed class OrderQueries(OrderingDbContext context) : IOrderQueries
{
    public async Task<IReadOnlyList<OrderSummaryResponse>> GetMyOrdersAsync(...)
    {
        var orders = await context.Orders
            .AsNoTracking()                                     // ← READS kenarı
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);
```

Bu ayrım bilinçli — `docs/hafta-2-notlar.md:17-21`: okuma tarafı `AsNoTracking` + doğrudan DTO projeksiyonu ile aggregate'i baypas eder, "sorgu durum değiştirmediği için bu bir ihlal değildir."

### 7.3 Handler'da DbContext YOK — mimari testle zorlanıyor

`Application` katmanında `DbContext` tipinde parametre/alan araması **sıfır sonuç** verdi. Bu tesadüf değil: `Application_should_not_depend_on_ef_core` adında bir NetArchTest testi bunu zorluyor (`docs/hafta-2-notlar.md:21-22`, `tests/ModularCommerce.ArchitectureTests/`).

Handler'lar sadece interface görüyor:

```csharp
// src/Modules/Ordering/.../Orders/Checkout/CheckoutHandler.cs:12-19
public sealed class CheckoutHandler(
    IOrderRepository orders,          // ← Domain interface
    ICartService cartService,         // ← Cart.Contracts
    IProductReader productReader,     // ← Catalog.Contracts
    IStockReservationService stockReservation,  // ← Inventory.Contracts
    IPaymentService paymentService,   // ← Payment.Contracts
    IValidator<CheckoutCommand> validator,
    ILogger<CheckoutHandler> logger)
```

### 7.4 ⚠️ Interface problemi bu repoda ne kadar ciddi

Roadmap Faz 2'nin "interface problemi" bölümü tam olarak bu repoyu tarif ediyor. **İyi haber: implementasyonlar çoğunlukla tekil**, yani `ambiguous: true` işareti nadiren gerekecek. **Ama üç yerde çoklu implementasyon var:**

| Interface | Implementasyonlar | Sonuç |
|---|---|---|
| `ICartRepository` | `PostgresCartRepository` + `CachingCartRepository` (decorator) | 2 implementasyon — decorator gerçek DB'ye delege ediyor. İkisini de node yapıp `ambiguous` işaretlemek doğru davranış. |
| `IProductQueries` | `ProductQueries` + `CachingProductQueries` (decorator) | Aynı durum. |
| `IReservationStrategy` | 3 strateji (Naive / OptimisticConcurrency / RedisLock), config ile seçiliyor | `ReserveStockHandler.cs:9` bunu inject ediyor. Üçü de node olmalı — hangisinin aktif olduğu **runtime config'e bağlı**, statik analizde bilinemez. Dokümante edilecek bilinçli bir trade-off. |
| Contracts adapter'ları (`ICartService`, `IProductReader`, `IStockReservationService`, `IPaymentService`, `IOrderReservationReconciler`) | Modül başına 1 adapter, `<Module>.Infrastructure/ContractAdapters/` | Tekil — sorunsuz. **Bunlar modüller arası senkron çağrı köprüsü**, `CALLS` kenarları için kritik. |

---

## 8. FlowLens roadmap'ine önerilen düzeltmeler

Keşiften çıkan, roadmap'te **yazılanla gerçeğin ayrıştığı** noktalar:

| Roadmap'te yazan | Gerçek | Öneri |
|---|---|---|
| Faz 2: "`Publish<T>()` görünce generic type argument'ı yakala" | Generic `Publish<T>` **yok**, tip-silinmiş `Publish(obj, Type)` var | `IIntegrationEventMapper` registry'lerindeki `typeof(...)` ifadelerini oku (§6.3) |
| Faz 3: "DbContext'i design-time factory ile örnekle" | Design-time factory **yok** | `new DbContextOptionsBuilder<T>().UseNpgsql(sahte-cs)` ile elle kur (§5.1 seçenek A) |
| Faz 3: "EF Core `IModel`" (tekil) | **8 ayrı DbContext**, 8 ayrı `IModel` | Modül başına döngü; Discovery'yi ayrı ele al (pgvector) |
| Veri modeli: `Handler` = "MediatR/Application layer" | MediatR yok, düz sınıf | `*Handler` soneki + `.Application` namespace'i ile tanı |
| Veri modeli: `Endpoint` = "Controller action / minimal API mapping" | Sadece minimal API, üstelik **3 katmanlı `MapGroup`** | §2.3'teki basitleştirilmiş prefix+suffix stratejisi |
| Faz 1: "solution'ı yükle" | 66 proje, 18'i test | Test projelerini filtrele (sahte `PUBLISHES` kenarları üretiyorlar, §6.3) |

### 8.1 Faz 2 için önerilen ilk endpoint

`POST /api/ordering/checkout` — çünkü tek zincirde her şeyi kapsıyor:

- Endpoint → Handler (`CheckoutHandler`)
- 4 farklı modüle **senkron cross-module** çağrı (`ICartService`, `IProductReader`, `IStockReservationService`, `IPaymentService`)
- Repository → DbContext → `WRITES` (`OrderRepository.AddAsync`)
- Domain event → outbox → **async cross-module** köprü (`OrderPaid` → Notification)
- Ambiguous interface (`IReservationStrategy`, 3 implementasyon)

Roadmap'in Faz 2 kabul kriterlerinin tamamı bu tek endpoint'te test edilebilir.

---

## 9. Bulamadıklarım

Dürüstlük kaydı — arandı, yoktu:

- **`IDesignTimeDbContextFactory`** — hiçbir implementasyon yok (§5).
- **MediatR ve türevleri** — sadece bir docs dosyasında adı geçiyor, kodda yok (§3).
- **Controller / MVC** — hiç yok (§2).
- **Generic `Publish<T>()`** — `src/` altında yok; sadece test projelerinde var (§6.3).
- **`dotnet ef ... --startup-project` örneği** — `docs/` ve `README.md`'de açık komut satırı bulamadım. Migration'ların hangi startup-project ile üretildiğini **doğrulayamadım**; §5'teki "muhtemelen Host üzerinden" ifadesi bir çıkarımdır, kanıt değil.
- **Shipping modülünün DbContext'i / endpoint'i** — yok, modül bilinçli olarak boş kabuk (`ShippingModule.cs:16`, `:21-22` yorumları).
