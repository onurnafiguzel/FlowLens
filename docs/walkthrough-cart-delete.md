# Tek endpoint'in uçtan uca izi — `DELETE /api/cart/items/{productId:guid}`

Bu doküman aracın **ne bulduğunu** değil **nasıl bulduğunu** anlatır. Tek bir endpoint alınır ve
`graph.json`'daki kutusundan, o kutuyu üreten Roslyn çağrısına kadar her halka tek tek gösterilir.

Örnek rastgele seçilmedi. Bu endpoint zincirin ilginç adımlarının hemen hepsini tek başına taşıyor:
`MethodDeclarationSyntax` olmayan bir lambda, üç halkalı bir `MapGroup` zinciri, ortasında bir
extension method, bir arayüz belirsizliği, bir ternary'nin iki dalı, bir jsonb kolonu ve bir
ikinci sınıf kanıt.

Bu dokümandaki her `dosya:satır` referansı yazılırken dosyadan okunarak doğrulandı. Her ara sonuç
`graph.json` ile karşılaştırılabilir.

---

## 0. Önce varış noktası

`graph.json`'daki endpoint node'u — birebir:

```json
{
  "id": "endpoint:DELETE /api/cart/items/{productId:guid}",
  "kind": "Endpoint",
  "displayName": "DELETE /api/cart/items/{productId:guid}",
  "module": "Cart",
  "filePath": "src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs",
  "line": 57,
  "ambiguous": false,
  "truncated": false,
  "utility": false,
  "depth": 0,
  "rootKind": "Endpoint",
  "location": "src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:57"
}
```

Ondan çıkan **üç** kenar var, üçü de `Calls`:

```json
{"fromId": "endpoint:DELETE /api/cart/items/{productId:guid}",
 "toId": "ModularCommerce.Cart.Application.Carts.RemoveItem.RemoveItemHandler.HandleAsync(System.Guid, System.Guid, System.Threading.CancellationToken)",
 "kind": "Calls", "ambiguous": false, "mechanism": "None",
 "callSites": [{"filePath": ".../CartEndpoints.cs", "line": 63, "column": 32, "conditional": false}]}

{"fromId": "endpoint:DELETE /api/cart/items/{productId:guid}",
 "toId": "ModularCommerce.Shared.Infrastructure.Auth.ClaimsPrincipalExtensions.GetUserId(System.Security.Claims.ClaimsPrincipal)",
 "kind": "Calls", ..., "callSites": [{..., "line": 63, "column": 52, "conditional": false}]}

{"fromId": "endpoint:DELETE /api/cart/items/{productId:guid}",
 "toId": "ModularCommerce.Shared.Infrastructure.Endpoints.ResultExtensions.ToHttpResult<T>(ModularCommerce.Shared.Kernel.Result<T>)",
 "kind": "Calls", ..., "callSites": [{..., "line": 64, "column": 20, "conditional": false}]}
```

Buradan okunacak dört şey var, dördü de sonraki bölümlerde açıklanacak:

- Node `line: 57` diyor ama kenarların çağrı yerleri `63` ve `64` — **node'un satırı bildirim,
  kenarın satırı yazılış yeri.** İki farklı soru.
- Aynı satırda (63) iki farklı çağrı var ve **kolonla** ayrışıyorlar: 32 ve 52.
- `mechanism: "None"` — bir `Calls` kenarında mekanizma alanı boştur, kenar türü zaten her şeyi söyler.
- `rootKind: "Endpoint"` ve `depth: 0` — bu bir kök.

Ve kaynağın kendisi:

```csharp
// ModularCommerce: src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:57-65
        secured.MapDelete("/items/{productId:guid}", async (
            Guid productId,
            ClaimsPrincipal user,
            RemoveItemHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(user.GetUserId(), productId, cancellationToken);
            return result.ToHttpResult();
        });
```

Dikkat: ortada **hiçbir metot bildirimi yok**. Ne route string'i (`/api/cart/...`) bu dosyada tam
hâliyle yazılı, ne de `MapDelete`'in kendisi ModularCommerce'e ait. Zincir bunların ikisini de
çözmek zorunda.

---

## 1. Solution nasıl yükleniyor

### 1.1 İlk satır bir satır meselesi değil, bir **metot sınırı** meselesi

`FlowLens.Cli/Program.cs` yalnız 19 satır ve bu bilerek:

```csharp
// FlowLens: src/FlowLens.Cli/Program.cs:1-19
using FlowLens.Cli;
using Microsoft.Build.Locator;

// ---------------------------------------------------------------------------------------
// Nothing may precede this registration, and this file must not name a single MSBuild or
// Roslyn-workspace type. See SolutionLoader's docs for the full explanation: the JIT resolves
// every type named in a method body when that method is first called, so mentioning
// MSBuildWorkspace *anywhere in this file* would trigger the MSBuild assembly load before the
// resolver below is installed - even if RegisterDefaults is written on the line above it.
//
// Runner is a separate type, so its body is JIT-compiled only when RunAsync is first invoked,
// which is after this line has run.
// ---------------------------------------------------------------------------------------
if (!MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

return await Runner.RunAsync(args);
```

**Neden önemli:** `MSBuildWorkspace` ve `Microsoft.Build.*` assembly'leri NuGet paketiyle değil
**.NET SDK ile** gelir. Runtime'a nerede olduklarını öğreten tek şey `RegisterDefaults()`'un
kurduğu `AssemblyResolve` kancasıdır. Ama CLR bir metodu **ilk çağrıldığında** JIT'ler ve JIT
gövdedeki **tüm tipleri** çözer. Yani şu kod çalışmaz:

```csharp
MSBuildLocator.RegisterDefaults();      // hiç koşma şansı bulamaz
var ws = MSBuildWorkspace.Create();     // tip, Main JIT'lenirken çözülür → FileNotFoundException
```

Çözüm satır sırası değil, **ayrı metot**. Ve o metodun inline edilmemesi gerekiyor:

```csharp
// FlowLens: src/FlowLens.Cli/Runner.cs:35-43
    /// <summary>
    /// NoInlining is load-bearing, not decoration. If the JIT inlined this body into the
    /// top-level Main, the MSBuild types named here would be resolved while Main is compiled -
    /// that is, before MSBuildLocator.RegisterDefaults() has executed - and the process would
    /// die with a FileNotFoundException for Microsoft.Build.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> RunAsync(string[] args)
    {
```

`[MethodImpl(NoInlining)]` burada bir optimizasyon ayarı değil, **doğruluk şartı**.

### 1.2 Yükleme ve dört bağımsız hata sinyali

```csharp
// FlowLens: src/FlowLens.Core/SolutionLoader.cs:45-61
        var expectedProjectCount = CountProjectsInSolutionFile(solutionPath);

        var workspace = MSBuildWorkspace.Create();

        // Subscribe BEFORE opening. This is a live event with no replay buffer: a handler
        // attached after OpenSolutionAsync sees nothing, and the load looks clean when it was
        // not. RegisterWorkspaceFailedHandler replaces the obsolete WorkspaceFailed event and
        // hands back a registration to dispose.
        var collector = new DiagnosticCollector();
        var registration = workspace.RegisterWorkspaceFailedHandler(collector.OnWorkspaceFailed);

        var stopwatch = Stopwatch.StartNew();
        Solution solution;
        try
        {
            solution = await workspace.OpenSolutionAsync(solutionPath, progress, cancellationToken);
        }
```

| API | Ne döndürür |
|---|---|
| `MSBuildWorkspace.Create()` | `MSBuildWorkspace` — MSBuild değerlendirmesini yapacak workspace |
| `RegisterWorkspaceFailedHandler(handler)` | `IDisposable` — kayıt; **açmadan önce** takılmalı |
| `OpenSolutionAsync(path, progress, ct)` | `Solution` — 66 projelik immutable model |

**Kritik davranış:** `OpenSolutionAsync`, yüklenemeyen bir proje için **istisna fırlatmaz**. Sessizce
o projeyi atlar ve size iyi biçimli bir `Solution` verir. Bu yüzden `CountProjectsInSolutionFile`
(`SolutionLoader.cs:86-98`) var: `.sln` dosyasındaki `.csproj` geçen satırlar sayılır ve
`Solution.ProjectIds.Count` ile karşılaştırılır. Sinyaller dört tane ve birbirinden bağımsız:
`WorkspaceFailed` tanılamaları · proje sayısı karşılaştırması · `SupportsCompilation` ·
opsiyonel `GetDiagnostics`.

**Girdi:** `ModularCommerce.sln`. **Çıktı:** 66/66 proje, ~16-20 s.

Bundan sonrasının hiçbirinde MSBuild yok — her şey `Solution` nesnesi üzerinde.

---

## 2. Lambda nasıl bulunuyor

### 2.1 Aranan şey bir metot değil, bir **çağrı ifadesi**

ModularCommerce'in 24 modül endpoint'inin tamamı Minimal API lambda'sı. Bunları
`MethodDeclarationSyntax` ile aramak sıfır sonuç verir — Faz 1'in metot sayacı
(`MethodScanner.cs:14-18`) kendi yorumunda bunu açıkça yazıyor ve bu endpoint'lerin **hiçbirini**
görmez.

Aranan şey `InvocationExpressionSyntax`:

```csharp
// FlowLens: src/FlowLens.Core/EndpointDiscovery.cs:113-134
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = InvokedName(invocation);
            if (name is null)
            {
                continue;
            }

            var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            var (filePath, line) = SourceLocation.For(invocation, solutionDirectory);

            if (MapVerbs.TryGetValue(name, out var httpMethod))
            {
                // A1 step 2: verify by SYMBOL, not by name. A1b: never drop silently.
                if (symbol is null)
                {
                    eliminated.Add(new EliminatedCandidate(
                        Truncate(invocation.Expression.ToString()), filePath, line, "symbol-unresolved"));
                    continue;
                }
```

Bizim çağrımız için, adım adım:

| Adım | API | Somut sonuç |
|---|---|---|
| Aday bulma | `root.DescendantNodes().OfType<InvocationExpressionSyntax>()` | `secured.MapDelete("/items/{productId:guid}", async (…) => …)` |
| Ad okuma | `InvokedName` → `memberAccess.Name.Identifier.Text` (`:377-384`) | `"MapDelete"` |
| Fiil eşleme | `MapVerbs` sözlüğü (`:21-28`) | `"DELETE"` |
| **Sembolle doğrulama** | `semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol` (`:123`) | framework'ün `EndpointRouteBuilderExtensions.MapDelete` sembolü |
| Framework mü | `IsFrameworkEndpointExtension` (`:342-351`) | evet — kaynakta değil, namespace `Microsoft.AspNetCore.` ile başlıyor |
| Konum | `SourceLocation.For(invocation, dir)` | `("src/Modules/Cart/.../CartEndpoints.cs", 57)` |

**Adla değil sembolle doğrulama** kuralı bir tarz tercihi değil: `MapDelete` adında kendi metodunu
yazan bir kod tabanı endpoint listesine sızardı. Eşleşmeyen aday sessizce düşmez, `EliminatedCandidate`
olarak gerekçesiyle kaydedilir (`"symbol-unresolved"`, `"resolved-to-other-type:…"`,
`"no-enclosing-method"`).

### 2.2 Lambda'nın sembol kimliği **hiç yok** — zincirin en öğretici adımı

```csharp
// FlowLens: src/FlowLens.Core/EndpointDiscovery.cs:152-163
                mapCalls.Add(new MapCallSite(
                    EnclosingMethod: NodeId.Canonical(enclosing),
                    Origin: ResolveOrigin(Receiver(invocation), semanticModel, 0, cancellationToken),
                    HttpMethod: httpMethod,
                    RouteSuffix: ConstantStringArgument(invocation, 0, semanticModel, cancellationToken),
                    HandlerBody: invocation.ArgumentList.Arguments.Count > 1
                        ? invocation.ArgumentList.Arguments[1].Expression
                        : null,
                    DocumentId: document.Id,
                    FilePath: filePath,
                    Line: line,
                    Module: module));
```

`HandlerBody`, `MapDelete`'in **1 numaralı argümanı** — yani `ParenthesizedLambdaExpressionSyntax`
düğümünün kendisi. Ham sözdizimi, çözülmüş bir sembol değil.

`GetDeclaredSymbol` bu dosyada **hiç çağrılmıyor.** (Tüm `FlowLens.Core` içinde yalnız dört yerde
geçiyor: `EntityAccessAnalyzer.cs:324,334`, `PropertyWriteAnalyzer.cs:218`,
`ExternalCallDetector.cs:107`.) Lambda'nın hiçbir zaman bir sembol kimliği olmuyor. Bunun yerine:

- **Kimlik** route string'inden geliyor → `NodeId.ForEndpoint` (`NodeId.cs:68-69`),
- **Gövde** ham `SyntaxNode` olarak taşınıp yürüyücüye **metot gövdesiymiş gibi** veriliyor.

```csharp
// FlowLens: src/FlowLens.Core/CallGraphWalker.cs:133-138
        if (endpoint.HandlerBody is null)
        {
            _warnings.Add($"{endpoint.FilePath}:{endpoint.Line} - endpoint has no handler body to walk");
        }

        return WalkAsync(endpointNode, endpoint.HandlerBody, cancellationToken);
```

Bu neden çalışıyor: yürüyücünün iş birimi `WorkItem(string NodeId, SyntaxNode Body, int Depth)`
(`CallGraphWalker.cs:604`) ve gövdeye yapılan tek şey `.DescendantNodes()` çağırmak. Bir lambda
ifadesi bunun için bir metot bildirimi kadar iyidir. **Ontoloji lambda'yı tanımıyor; hiç tanıması
gerekmedi.**

---

## 3. Route string'i nasıl çıkıyor

`"/items/{productId:guid}"` dosyada yazılı. `"/api/cart"` başka bir dosyada. İkisini birleştirecek
yerel hiçbir bilgi yok — bu yüzden keşif **iki geçişli**.

### 3.1 Zincirin beş halkası

```csharp
// ModularCommerce: src/Bootstrapper/ModularCommerce.Host/Program.cs:85-88
foreach (var module in modules)
{
    module.MapEndpoints(app);
}
```

```csharp
// ModularCommerce: src/Modules/Cart/ModularCommerce.Cart.Api/CartModule.cs:42-47
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/cart");

        group.MapCartEndpoints();
    }
```

```csharp
// ModularCommerce: src/Modules/Cart/ModularCommerce.Cart.Api/Endpoints/CartEndpoints.cs:20-23
    public static void MapCartEndpoints(this IEndpointRouteBuilder group)
    {
        var secured = ((RouteGroupBuilder)group).MapGroup("")
            .RequireAuthorization();
```

Beş halka, dördü ayrı dosyada:

| # | Yer | Ne katıyor |
|---|---|---|
| 1 | `Program.cs:87` `module.MapEndpoints(app)` | `IModule` arayüzü — 9 modüle açılacak, prefix `"/"` |
| 2 | `CartModule.cs:44` `endpoints.MapGroup("/api/cart")` | `/api/cart` |
| 3 | `CartModule.cs:46` `group.MapCartEndpoints()` | **extension method** — prefix'i taşıyan halka |
| 4 | `CartEndpoints.cs:22` `((RouteGroupBuilder)group).MapGroup("")` | boş segment + bir cast + bir fluent çağrı |
| 5 | `CartEndpoints.cs:57` `MapDelete("/items/{productId:guid}")` | son ek |

### 3.2 Alıcı zincirini geri yürümek

```csharp
// FlowLens: src/FlowLens.Core/EndpointDiscovery.cs:214-252
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return ResolveOrigin(parenthesized.Expression, semanticModel, depth + 1, cancellationToken);

            // ((RouteGroupBuilder)group).MapGroup("") - the cast carries no routing meaning.
            case CastExpressionSyntax cast:
                return ResolveOrigin(cast.Expression, semanticModel, depth + 1, cancellationToken);

            case InvocationExpressionSyntax invocation:
            {
                var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (symbol is null)
                {
                    return BuilderOrigin.Unknown;
                }

                var receiver = Receiver(invocation);

                if (symbol.Name == MapGroupName && IsFrameworkEndpointExtension(symbol))
                {
                    var inner = ResolveOrigin(receiver, semanticModel, depth + 1, cancellationToken);
                    if (inner.Kind == BuilderOriginKind.Unknown)
                    {
                        return BuilderOrigin.Unknown;
                    }

                    var segment = ConstantStringArgument(invocation, 0, semanticModel, cancellationToken);
                    return segment is null
                        ? BuilderOrigin.Unknown
                        : inner with { RelativePrefix = RouteText.Combine(inner.RelativePrefix, segment) };
                }

                // Fluent pass-through (RequireAuthorization, WithName, ...): the value is still
                // the same route builder, so keep walking the receiver.
                return IsRouteBuilderType(symbol.ReturnType)
                    ? ResolveOrigin(receiver, semanticModel, depth + 1, cancellationToken)
                    : BuilderOrigin.Unknown;
            }
        }
```

4. halka bu kodun üç dalını birden çalıştırıyor: `RequireAuthorization` fluent pass-through
(`:249-251`), `MapGroup("")` segment ekleme (`:233-245`), `(RouteGroupBuilder)` cast'i soyma
(`:220-221`). Sonunda `group` bir `IParameterSymbol` olarak bulunuyor →
`BuilderOrigin(Parameter, "/")` (`:260`).

Route suffix `GetConstantValue` ile okunuyor, literal eşlemesiyle değil
(`EndpointDiscovery.cs:366`) — böylece `const string Route = "..."` de çözülür.

### 3.3 Birleştirme: segment bazlı, özel vaka yok

```csharp
// FlowLens: src/FlowLens.Core/RouteText.cs:14-22
    public static string Combine(params string?[] parts)
    {
        var segments = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .SelectMany(p => p!.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }
```

Parçalar `/` üzerinden segmentlere bölündüğü için `MapGroup("")` **hiçbir özel vaka olmadan**
kayboluyor, çift `//` çökertiliyor ve `{productId:guid}` bir segment olarak dokunulmadan geçiyor.

### 3.4 Prefix'in dosyalar arası yayılması ve **üç parça** kuralı

Pass 2 bir BFS: kökü olan builder'lardan başlar, prefix'i her yayılım noktasından hedef metoda
taşır (`RoutePrefixResolver.cs:47-74`). Sonunda her map çağrısı için:

```csharp
// FlowLens: src/FlowLens.Core/RoutePrefixResolver.cs:107-117
                    foreach (var prefix in prefixes.Order(StringComparer.Ordinal))
                    {
                        // Three parts, not two. The incoming prefix stops at the method's
                        // parameter; anything the call chained on top of it - as in
                        // endpoints.MapGroup("/api").MapPost(...) - lives on the call's own origin
                        // and would otherwise be dropped.
                        endpoints.Add(Build(
                            call,
                            RouteText.Combine(prefix, call.Origin.RelativePrefix, call.RouteSuffix),
                            multiMount));
                    }
```

Bizim endpoint için: `Combine("/api/cart", "/", "/items/{productId:guid}")` → **`/api/cart/items/{productId:guid}`**.

**Üçüncü parça neden var:** gelen prefix metodun parametresinde durur. `endpoints.MapGroup("/api").MapPost(…)`
gibi çağrının kendi zincirlediği prefix `call.Origin.RelativePrefix`'te yaşar ve iki parçalı birleştirmede
düşerdi. Bu Faz 2'de sentetik bir testin yakaladığı gerçek bir hataydı — ModularCommerce prefix'i her
zaman bir yerel değişkende tuttuğu için gerçek repoda **görünmüyordu**.

### 3.5 Reduced vs unreduced — bu endpoint'in prefix'i buna bağlı

3. halka (`group.MapCartEndpoints()`) bir extension method ve Roslyn iki farklı sembol veriyor:

| Nerede | Ne döner |
|---|---|
| Çağrı yerinde, `GetSymbolInfo` | **reduced** form: `MapCartEndpoints()` — `this` parametresi düşmüş |
| Metodun içinde, `GetEnclosingSymbol` | **unreduced** form: `MapCartEndpoints(IEndpointRouteBuilder)` |

İkisi farklı sembol, dolayısıyla farklı sözlük anahtarı. Normalleştirilmezse prefix hedefini hiç
bulamaz ve **extension method içinde bildirilen her endpoint route'unu kaybeder** — ki
ModularCommerce'in tamamı böyle:

```csharp
// FlowLens: src/FlowLens.Core/NodeId.cs:54-58
    public static IMethodSymbol Canonical(IMethodSymbol symbol) =>
        (symbol.ReducedFrom ?? symbol).OriginalDefinition;

    public static string ForMethod(IMethodSymbol symbol) =>
        Canonical(symbol).ToDisplayString(MemberFormat);
```

`ReducedFrom` yalnız reduced extension method'lar için doludur, başka her şeyde `null` — bu yüzden
`?? symbol`. `OriginalDefinition` ise generic örneklemeleri (`Select<A,B>`, `Select<C,D>`) tek
tanıma çökertir; onsuz ziyaret kümesi yakınsamaz.

`NodeId.Canonical` sembolün **kimlik olarak kullanıldığı her yerde** çağrılıyor —
`EndpointDiscovery.cs:153`, `CallGraphWalker.cs:258`, `ImplementationResolver.cs:74`.

---

## 4. Lambda gövdesinden handler'a

Yürüyücünün ana döngüsünün özü:

```csharp
// FlowLens: src/FlowLens.Core/CallGraphWalker.cs:249-258
            _invocationsExamined++;

            var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);

            var bound = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            // Canonical form immediately: a call site sees the reduced extension method and the
            // declaration sees the unreduced one, and identity has to agree across both.
            var target = bound is null ? null : NodeId.Canonical(bound);
```

`GetSymbolInfo` iki alan taşır: `Symbol` yalnız derleyici **tek** aday bağladığında dolu olur;
overload çözümü başarısızsa `CandidateSymbols` devreye girer ve bu durum kenarı `ambiguous`
işaretler (`:298`).

```csharp
// FlowLens: src/FlowLens.Core/CallGraphWalker.cs:286-319
            // Framework and NuGet methods are out of scope: the graph describes project code.
            if (!SourceLocation.IsInSource(target))
            {
                _frameworkFiltered++;
                continue;
            }
            ...
            var ambiguousCall = info.Symbol is null;
            var targetId = AddMethodNode(target, item.Depth + 1, ambiguous: ambiguousCall);

            // Where the call is WRITTEN. The node's own line is the callee's declaration, which is
            // a different question and the one the diagram was silently answering before.
            var site = SourceLocation.WithColumn(invocation, solutionDirectory);

            AddEdge(
                item.NodeId,
                targetId,
                EdgeKind.Calls,
                ambiguous: ambiguousCall,
                callSite: new CallSite(
                    site.FilePath, site.Line, site.Column, IsConditional(invocation, item.Body)));

            if (ImplementationResolver.NeedsResolution(target))
            {
                await ExpandInterfaceAsync(target, targetId, item.Depth, queue, cancellationToken);
                continue;
            }

            EnqueueBody(target, targetId, item.Depth + 1, queue);
```

Lambda gövdesindeki `handler.HandleAsync(...)` için:

1. `GetSymbolInfo` → `RemoveItemHandler.HandleAsync(Guid, Guid, CancellationToken)`.
   `handler` somut bir sınıf parametresi, overload yok → `info.Symbol` dolu.
2. `NodeId.Canonical` → değişmiyor (`ReducedFrom` null, `OriginalDefinition` kendisi).
3. `IsInSource` → true, framework filtresine takılmıyor.
4. `NeedsResolution` → **false** (`ContainingType.TypeKind` `Class`, `Interface` değil).
5. Node depth **1**'de kuruluyor; `kind` = `Handler` (`GraphModel.cs:238-261`: adı `Handler` ile
   bitiyor **ve** namespace `.Application` içeriyor).
6. Çağrı yeri `CartEndpoints.cs:63`, kolon 32.
7. `EnqueueBody` — özyinelemenin gerçekleştiği yer:

```csharp
// FlowLens: src/FlowLens.Core/CallGraphWalker.cs:467-483
    private void EnqueueBody(IMethodSymbol method, string nodeId, int depth, Queue<WorkItem> queue)
    {
        // First visit wins: the body is walked once, but every edge into it is still recorded.
        if (!_visitedBodies.Add(nodeId))
        {
            return;
        }

        var declaration = method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .FirstOrDefault();

        if (declaration is not null)
        {
            queue.Enqueue(new WorkItem(nodeId, declaration, depth));
        }
    }
```

`DeclaringSyntaxReferences` bir sembolden **geri sözdizimine** geçmenin yoludur ve metadata
sembolleri için boş dizi döner — yani framework metotları buraya gelse bile sessizce kuyruğa
girmezdi. Sonuç: `RemoveItemHandler.cs:9`'daki `MethodDeclarationSyntax` kuyruğa giriyor ve
lambda ile aynı şekilde işleniyor.

> **Semantic model nereden geliyor:** `document.GetSemanticModelAsync` (`CallGraphWalker.cs:581`),
> `Compilation.GetSemanticModel` değil. Yorum gerekçeyi yazıyor (`:579-580`): birincisi workspace
> tarafından cache'leniyor, ikincisi her çağrıda yenisini kuruyor.

---

## 5. Üç çağrı ve kaç kutu ürettikleri

### 5.1 Handler gövdesi

```csharp
// ModularCommerce: src/Modules/Cart/.../RemoveItem/RemoveItemHandler.cs:7-34
public sealed class RemoveItemHandler(ICartRepository carts)
{
    public async Task<Result<CartResponse>> HandleAsync(
        Guid customerId,
        Guid productId,
        CancellationToken cancellationToken)
    {
        var getResult = await carts.GetAsync(customerId, cancellationToken);
        ...
        var persistResult = cart.IsEmpty
            ? await carts.RemoveAsync(customerId, cancellationToken)
            : await carts.SaveAsync(cart, cancellationToken);
```

`carts` alanının tipi `ICartRepository`. Üç çağrının üçü de **arayüz üyesine** bağlanıyor —
`PostgresCartRepository.GetAsync`'e değil. Bu statik analizin yapısal sınırı: DI'ın runtime'da hangi
implementasyonu enjekte edeceği kaynakta yazmıyor.

Handler'ın `graph.json`'daki sekiz giden kenarı:

| Hedef | Çağrı yerleri (satır, kolon, koşullu) |
|---|---|
| `CartResponse.FromCart(Cart)` | (41, 31, hayır) |
| `Cart.RemoveItem(Guid)` | (26, 28, hayır) |
| `CartErrors.ItemNotFound(Guid)` | (23, 49, **evet**) |
| `ICartRepository.GetAsync(…)` | (14, 31, hayır) |
| `ICartRepository.RemoveAsync(…)` | (33, 21, **evet**) |
| `ICartRepository.SaveAsync(…)` | (34, 21, **evet**) |
| `Result.Failure<T>(Error)` | (17,20) (23,20) (29,20) (38,20) — **hepsi evet** |
| `Result.Success<T>(T)` | (41, 16, hayır) |

İki şey görünüyor:

- **`Result.Failure<T>` tek kenar, dört çağrı yeri.** Graph `(from, to, kind)` başına tek kenar
  tutar ama tekrarı düşürmez, **birleştirir** (`CallGraphWalker.cs:535-549`). `<T>` ise
  `OriginalDefinition`'ın örneklemeleri çökertmesi.
- **`RemoveAsync` ve `SaveAsync` `conditional: true`, `GetAsync` değil.** İkisi aynı ternary'nin
  iki dalı ve **birbirini dışlar** — ikisi birden koşmaz. `IsConditional`
  (`CallGraphWalker.cs:332-364`) ata düğümleri gövdeye kadar yürüyüp `ConditionalExpressionSyntax`
  görüyor.

> Bu vaka `CallSite` kaydının doküman yorumunda **adıyla** geçiyor:
> *"Measured because RemoveItemHandler's two persistence calls are the two arms of one ternary and
> exclude each other."* (`GraphModel.cs:199-203`)

### 5.2 Kaç kutu? — sorunun düzeltilmesi

Üretilmiş akış diyagramında her çağrının **iki** kutusu var (`CachingCartRepository.X` ve
`PostgresCartRepository.X`). Ama `graph.json`'da her çağrı **üç** node üretiyor:

```
RemoveItemHandler.HandleAsync                       depth 1
  └── ICartRepository.GetAsync                      depth 2   ← diyagramda YOK
        ├── CachingCartRepository.GetAsync          depth 3   ambiguous: true
        └── PostgresCartRepository.GetAsync         depth 3   ambiguous: true
```

Fark sunum katmanından geliyor: doküman üreteci arayüz bildirimlerini gizliyor ve bunu
söylüyor da — `out/flows/delete-api-cart-items-productid-guid.md`'nin *"Diyagram neyi göstermiyor"*
bölümü **"3 arayüz bildirimi"** gizlendiğini yazıyor (gösterilen 10 node, ham yürüyüş 38).

**Kutu sayısı ≠ node sayısı.** Diyagram bir görünüm, graph ise kayıt.

### 5.3 Arayüz genişletmesi

```csharp
// FlowLens: src/FlowLens.Core/CallGraphWalker.cs:366-393
    /// <summary>
    /// A call bound to an interface member names the contract, not the implementation DI will
    /// supply. Every implementation is added and the edge is marked ambiguous when there is more
    /// than one - missing a real path is worse than carrying an extra one.
    /// </summary>
    private async Task ExpandInterfaceAsync(
        IMethodSymbol interfaceMember,
        string interfaceNodeId,
        int depth,
        Queue<WorkItem> queue,
        CancellationToken cancellationToken)
    {
        var result = await implementationResolver.ResolveAsync(interfaceMember, cancellationToken);

        if (result.Implementations.Count == 0)
        {
            _warnings.Add(
                $"no implementation found for {interfaceMember.ToDisplayString(NodeId.MemberFormat)}");
            return;
        }

        foreach (var implementation in result.Implementations)
        {
            var implementationId = AddMethodNode(implementation, depth + 2, result.Ambiguous);
            AddEdge(interfaceNodeId, implementationId, EdgeKind.Calls, result.Ambiguous);
            EnqueueBody(implementation, implementationId, depth + 2, queue);
        }
    }
```

Dikkat: `AddEdge` burada **`callSite` parametresi almıyor**. `graph.json`'daki altı
arayüz→implementasyon kenarının hepsi `"callSites": []`. Bu bilinçli:

> *"Empty for edges that are not written anywhere in source — an interface-to-implementation edge
> is DI resolution, not a call site, and inventing one would be a fabricated claim."*
> (`GraphModel.cs:216-219`)

Üretilmiş akış dokümanındaki *"kaynakta bir çağrı ifadesi yok … çağrı yeri kaydedilmedi"* satırının
sebebi tam olarak bu.

### 5.4 İki farklı belirsizlik, tek alan adı

`graph.json`'da:

- `ICartRepository.GetAsync` node'u → `"ambiguous": false`
- `CachingCartRepository.GetAsync` ve `PostgresCartRepository.GetAsync` → `"ambiguous": true`

Aynı alan, iki farklı anlam:

| Nerede | Kaynak | Anlamı |
|---|---|---|
| Arayüz node'u | `var ambiguousCall = info.Symbol is null;` (`CallGraphWalker.cs:298`) | **overload** çözümü belirsiz |
| Implementasyon node'u | `Ambiguous: implementations.Count > 1` (`ImplementationResolver.cs:107`) | **hangi implementasyon koşuyor** belirsiz |

Arayüz çağrısının kendisi tek bir üyeye net bağlandığı için birincisi `false`; iki implementasyon
olduğu için ikincisi `true`.

---

## 6. İki repository'ye nasıl ulaşılıyor

### 6.1 Çözümleyici: yalnız `SymbolFinder`, DI hiç okunmuyor

```csharp
// FlowLens: src/FlowLens.Core/ImplementationResolver.cs:67-94
    public static bool NeedsResolution(IMethodSymbol symbol) =>
        symbol.ContainingType?.TypeKind == TypeKind.Interface;

    public async Task<ImplementationResult> ResolveAsync(
        IMethodSymbol interfaceMember,
        CancellationToken cancellationToken = default)
    {
        var key = NodeId.Canonical(interfaceMember);
        var cacheKey = NodeId.ForMethod(key);

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            CacheHits++;
            return cached;
        }

        SymbolFinderCalls++;

        // FindImplementationsAsync works on interface MEMBERS directly - it returns the
        // implementing members, so there is no need to find the implementing type first and
        // then match the member by signature.
        var found = await SymbolFinder.FindImplementationsAsync(
            key, solution, _scope, cancellationToken);

        var implementations = found
            .OfType<IMethodSymbol>()
            .Where(SourceLocation.IsInSource)
            .ToList();
```

`FindImplementationsAsync` arayüz **üyesi** üzerinde çalışır ve implement eden **üyeleri** döndürür —
önce tipi bulup sonra imzayla eşleştirmek gerekmiyor. Bizim vakada iki üye:
`CachingCartRepository.GetAsync` ve `PostgresCartRepository.GetAsync`.

`NeedsResolution` bir maliyet kapısı: yalnız arayüz üyeleri `SymbolFinder`'a gider. Ölçüldü — Faz 2'de
checkout zincirinin tamamı **23 `SymbolFinder` çağrısı** yaptı; somut çağrılar hiç dokunmuyor.
Zincir yürüyüşünün tahminden ~40× hızlı çıkmasının sebebi buydu.

### 6.2 Cache neden **isimle** anahtarlanıyor

```csharp
// FlowLens: src/FlowLens.Core/ImplementationResolver.cs:49-52
    // Keyed by canonical name, not by symbol. Symbol identity is per-COMPILATION: the same
    // interface member observed from two calling projects is two unequal symbols, so a symbol-keyed
    // cache would miss on nearly every call and re-run a solution-wide search each time.
    private readonly Dictionary<string, ImplementationResult> _cache = new(StringComparer.Ordinal);
```

Bu, Faz 2'nin en pahalı dersinin doğrudan sonucu: **sembol kimliği compilation başınadır, solution
başına değil.** `ICartRepository.GetAsync`, `Cart.Application` compilation'ından bakıldığında ile
`Cart.Infrastructure` compilation'ından bakıldığında iki farklı `IMethodSymbol` örneğidir ve
`SymbolEqualityComparer.Default` bunları eşit görmez.

Bu vakada somut olarak görünüyor: aynı arayüz üyesi hem `RemoveItemHandler`'dan (Application) hem
`CachingCartRepository`'den (Infrastructure) çağrılıyor. Sembolle anahtarlansaydı ikincisi cache'i
ıskalar ve solution çapında ikinci bir arama koşardı.

### 6.3 Decorator zinciri **modellenmiyor** — kendiliğinden çıkan bir döngü

FlowLens `CartModule.cs:29-32`'yi okumuyor:

```csharp
// ModularCommerce: src/Modules/Cart/ModularCommerce.Cart.Api/CartModule.cs:27-32
        services.AddScoped<PostgresCartRepository>();
        services.AddSingleton<ICartCache, RedisCartCache>();
        services.AddScoped<ICartRepository>(
            sp => new CachingCartRepository(
                 sp.GetRequiredService<PostgresCartRepository>(),
                 sp.GetRequiredService<ICartCache>()));
```

Zincir şuradan çıkıyor: `CachingCartRepository`'nin gövdesindeki `inner.GetAsync(...)` çağrısı
(`CachingCartRepository.cs:17`) yine `ICartRepository`'ye bağlanıyor ve **aynı** çözümlemeden
geçiyor. `graph.json`'da:

```
ICartRepository.GetAsync        → CachingCartRepository.GetAsync    (arayüz çözümü, callSites: [])
CachingCartRepository.GetAsync  → ICartRepository.GetAsync          (gerçek çağrı, CachingCartRepository.cs:17:32)
ICartRepository.GetAsync        → PostgresCartRepository.GetAsync   (arayüz çözümü, callSites: [])
```

Yani graph'ta gerçek bir **2-cycle** var. Sonlanmayı sağlayan şey döngü tespiti değil,
`EnqueueBody`'deki `_visitedBodies` kümesi (`CallGraphWalker.cs:470`): gövde bir kez yürünür, ama
ona giren her kenar yine kaydedilir.

**Bu vakada "hepsini listele" politikası doğru cevap veriyor** — decorator zincirinde iki
implementasyon da gerçekten koşar. Config anahtarıyla seçilen arayüzlerde (`IReservationStrategy`)
aynı politika aşırı-yaklaşımdır ve kayıtlıdır (L3/L11).

> `RedisCartCache` bu akışta hiçbir node üretmiyor. `ExternalCall` yalnız `HttpClient` çağrılarını
> tanıyor; ilişkisel olmayan depolar ontolojide yok (**L17**). Kayıp sessiz değil — sınır kayıtlı.

---

## 7. `cart.carts` ve kolonları nereden geliyor

Buraya kadar her şey Roslyn'di. Veri katmanı **iki bağımsız kaynağın** birleşmesi: EF Core'un
`IModel`'i (tablo ve kolon adları) ve Roslyn (kim yazıyor, kim okuyor).

### 7.1 EF tarafı — hedefin `IModel`'i gerçekten kuruluyor

`EfProbe`, hedefin **derlenmiş** assembly'lerini özel bir `AssemblyLoadContext` ile bu sürece
yükleyip `DbContext`'i gerçekten örnekliyor:

```csharp
// FlowLens: src/FlowLens.Core/Ef/EfProbe.cs:348-358
    private static DbContext Instantiate(Type contextType)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;

        builder.UseNpgsql(
            ProbeConnectionString,
            npgsql => npgsql.SetPostgresVersion(ProbePostgresVersion));

        return (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
    }
```

**Veritabanına hiç dokunulmuyor.** Bağlantı dizesi iyi biçimli ama hiç açılmıyor: model kurmak
kaynağın saf bir fonksiyonudur (`ModelSource → ModelBuilder → OnModelCreating → conventions →
validators`). `EnsureCreated`, `Migrate`, `CanConnect` ya da herhangi bir `IQueryable`
enumerasyonu asla çağrılmıyor.

```csharp
// FlowLens: src/FlowLens.Core/Ef/EfProbe.cs:191-223 (kısaltıldı)
    private static EfModelSnapshot ReadOne(Type contextType)
    {
        using var context = Instantiate(contextType);
        var model = context.Model;

        var entities = new List<EfEntity>();
        var jsonContainers = CollectJsonContainerColumns(model);

        foreach (var entityType in model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var properties = new List<EfProperty>();
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);

            CollectProperties(entityType, storeObject, clrType, properties, null, null);
            ...
            entities.Add(new EfEntity(
                ClrTypeName: FullName(clrType) ?? clrType.Name,
                Schema: entityType.GetSchema(),
                TableName: entityType.GetTableName(),
                OwnerClrTypeName: FullName(entityType.FindOwnership()?.PrincipalEntityType.ClrType),
                IsMappedToJson: entityType.IsMappedToJson(),
                Properties: properties));
        }
```

`"cart"` + `"carts"` → `"cart.carts"` string'i ilk kez `EfEntity.QualifiedTableName`'de oluşuyor
(`EfModelSnapshot.cs:58-60`).

Kaynak taraf:

```csharp
// ModularCommerce: src/Modules/Cart/.../Persistence/CartDbContext.cs:8-16
    public const string Schema = "cart";

    public DbSet<CartRecord> Carts => Set<CartRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CartDbContext).Assembly);
    }
```

```csharp
// ModularCommerce: src/Modules/Cart/.../Persistence/Configurations/CartConfiguration.cs:8-19
    public void Configure(EntityTypeBuilder<CartRecord> builder)
    {
        builder.ToTable("carts");

        builder.HasKey(c => c.CustomerId);
        builder.Property(c => c.CustomerId).ValueGeneratedNever(); // müşteri kimliği = sepet kimliği

        builder.Property(c => c.UpdatedAtUtc).IsRequired();

        // Kalemler tek bir jsonb kolonuna (OwnsMany.ToJson, EF10) — sepet bir bütün olarak okunur/yazılır.
        builder.OwnsMany(c => c.Items, items => items.ToJson());
    }
```

> **EF'e dokunan tek dosya `EfProbe.cs`.** Bu bir disiplin değil, derleyiciye bağlı bir kural:
> `EfProbeArchitectureTests` `src/FlowLens.Core` altındaki her `.cs`'i tarar ve EF/Npgsql `using`'i
> olan tek dosyanın `EfProbe.cs` olmasını zorunlu kılar. Dışarı yalnız serileştirilebilir bir
> snapshot çıkar; EF tipleri sınırı geçmez.

### 7.2 `Items` kolonu — hiçbir tarafın `GetProperties()`'inde yok

`OwnsMany(...).ToJson()` bir tuzak: `CartItemRecord`'un üyeleri JSON alanı, `CartRecord.Items` ise
bir *navigation*, property değil. Kolon iki tarafın da property listesinde **yok**:

```csharp
// FlowLens: src/FlowLens.Core/Ef/EfProbe.cs:234-246 (doküman yorumu)
    /// Needed because such a collection is one column on the owner's table and that column belongs
    /// to neither side's <c>GetProperties()</c>: the owned type's members are JSON fields, and the
    /// owner's navigation is a navigation, not a property. So the column simply did not exist in the
    /// snapshot - measured: <c>cart.carts.Items</c> was absent while <c>record.Items = items</c> was
    /// analysed and then discarded as "written but not mapped to a column".
    ///
    /// The synthetic property is named after the NAVIGATION, because that is what C# assigns to and
    /// therefore the only name an analyzer can match.
```

Sentetik kolon burada üretiliyor:

```csharp
// FlowLens: src/FlowLens.Core/Ef/EfProbe.cs:252-268
        foreach (var entityType in model.GetEntityTypes())
        {
            if (!entityType.IsMappedToJson()
                || entityType.FindOwnership() is not { } ownership
                || ownership.PrincipalToDependent?.Name is not { } navigation
                || entityType.GetContainerColumnName() is not { Length: > 0 } column)
            {
                continue;
            }

            // Only the outermost JSON type owns a column of the table. A nested one lives inside
            // that same document, so claiming a second column for it would invent one.
            var owner = ownership.PrincipalEntityType;
            if (owner.IsMappedToJson())
            {
                continue;
            }
```

Adın **navigation**'dan alınması kritik: analizci `record.Items = items` ifadesini görecek ve
eşleştirebileceği tek isim `Items`.

Bunun karşılığı `graph.json`'un diagnostics'inde duruyor — `CartItemRecord`'un kendi üyelerinin
kolonu **yok** ve bu sessizce düşmüyor:

```
property written but not mapped to a column: CartItemRecord.AddedAtUtc at .../PostgresCartRepository.cs:41 (no column)
property written but not mapped to a column: CartItemRecord.ProductId  at .../PostgresCartRepository.cs:41 (no column)
property written but not mapped to a column: CartItemRecord.Quantity   at .../PostgresCartRepository.cs:41 (no column)
```

### 7.3 Roslyn tarafı — kim yazıyor

```csharp
// ModularCommerce: src/Modules/Cart/.../Persistence/PostgresCartRepository.cs:44-62
            var record = await context.Carts
                .FirstOrDefaultAsync(c => c.CustomerId == cart.CustomerId, cancellationToken);

            if (record is null)
            {
                context.Carts.Add(new CartRecord
                {
                    CustomerId = cart.CustomerId,
                    Items = items,
                    UpdatedAtUtc = DateTime.UtcNow,
                });
            }
            else
            {
                record.Items = items;
                record.UpdatedAtUtc = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(cancellationToken);
```

**DbSet tanıma isimle değil statik tiple:**

```csharp
// FlowLens: src/FlowLens.Core/Ef/EntityAccessAnalyzer.cs:489-501
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            return null;
        }

        // Metadata name plus namespace, rather than a rendered display string: display formats
        // differ on how they spell the type parameter and would silently stop matching.
        var definition = named.ConstructedFrom;
        if (definition.MetadataName != "DbSet`1"
            || definition.ContainingNamespace?.ToDisplayString() != "Microsoft.EntityFrameworkCore")
        {
            return null;
        }
```

Bu yüzden `DbSet<CartRecord> Carts => Set<CartRecord>()` (ifade gövdeli property) sıradan bir
auto-property'yle **aynı** analiz ediliyor.

**Yazma fiilleri sabit bir sözlük, ve alt kümesi önemli:**

```csharp
// FlowLens: src/FlowLens.Core/Ef/EntityAccessAnalyzer.cs:64-73
    /// <summary>
    /// The subset of <see cref="WriteMethods"/> that writes every column of the row rather than
    /// the ones an assignment names: an INSERT lists all columns, and Update marks every property
    /// modified. Remove/Attach/ExecuteDelete do not, and ExecuteUpdate names its own.
    /// </summary>
    private static readonly HashSet<string> WholeRowMethods = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Update", "UpdateRange",
    };
```

`Remove` bilerek bu listede **yok** — bu yüzden `RemoveAsync` hiçbir kolon kenarı üretmiyor. Bir
DELETE hiçbir kolonu yazmaz.

**Kolon yazmasının mekanizması bir koşula bağlı:**

```csharp
// FlowLens: src/FlowLens.Core/Ef/PropertyWriteAnalyzer.cs:127-133
        // An assignment inside the entity's own constructor is how aggregates set their initial
        // state; Order.Create would otherwise appear to write no columns at all.
        var mechanism = IsInsideConstructorOf(site, declaringType, semanticModel, cancellationToken)
            ? EdgeMechanism.EntityConstructorAssignment
            : EdgeMechanism.PropertyAssignment;

        var (declarationFile, declarationLine) = DeclarationOf(symbol!, solutionDirectory);
```

`PostgresCartRepository.SaveAsync` bir constructor değil → **`PropertyAssignment`**. Ve iki farklı
konum ayrışıyor: node'un `filePath`/`line`'ı **bildirim** (`CartRecord.cs:4/5/6`), kenarın
`evidence`'ı ise **yazma yeri** (`PostgresCartRepository.cs:51/52/53`).

### 7.4 Birleşme: `DataLayerOverlay`

Veri katmanı yürüyüşün **içinde** değil, üstünde ayrı bir geçiş olarak üretiliyor
(`DataLayerOverlay.cs:10-15`): böylece traversal EF'den tamamen bağımsız kalıyor ve veri katmanı
yalnız bir giriş noktasından **erişilebilen** kod için türetiliyor.

Sıra önemli:

```csharp
// FlowLens: src/FlowLens.Core/DataLayerOverlay.cs:120-131
            var writes = _propertyWrites.Analyze(body, semanticModel, solutionDirectory, cancellationToken);

            foreach (var column in writes.Columns)
            {
                AddColumnWrite(nodeId, column, nodes, edges, seenEdges, named, cancellationToken);
            }

            // Last, so every precisely named column is already known.
            foreach (var entry in wholeRows)
            {
                AddRowColumnWrites(nodeId, entry, nodes, edges, seenEdges, named, cancellationToken);
            }
```

`named` kümesi yüzünden cart kolonları `PropertyAssignment` taşıyor, `RowInsert` değil: kesin olarak
adlandırılmış bir kolon kendi kenarını korur, satır kuralı ona dokunmaz.
*"`Items = ... at PostgresCartRepository.cs:52`"* iddiası *"satır yazıldı"* iddiasından **kesinlikle
daha iyidir** ve bir kolon başına yalnız bir kenar cevabı taşımalıdır.

### 7.5 `Column → Table`, asla tersi

```csharp
// FlowLens: src/FlowLens.Core/DataLayerOverlay.cs:520-535
        // Column -> Table, and deliberately NOT Table -> Column.
        //
        // The relationship has to be an edge: leaving it implicit in the id string would force
        // every consumer to parse "column:ordering.orders.Status" to learn which table it belongs
        // to, which is exactly the string-handling a graph exists to replace.
        //
        // The direction is the load-bearing part. Table -> Column reads like a mapping but behaves
        // like reachability: a read-only endpoint that reaches a table would then reach every
        // column any writer of that table touches, and GET /api/catalog/products claimed six
        // column writes when that edge existed. Column -> Table has no such effect - forward
        // traversal from a writer reaches the column and then its table, which is already true by
        // another route, while a reader of the table still reaches no columns at all.
        Add(edges, seen, new Edge(
            columnId, tableId, EdgeKind.MapsTo,
            $"EF Core model: {write.EntityClrTypeName}.{write.PropertyName} -> {write.QualifiedTableName}",
            Mechanism: EdgeMechanism.EfModelMapping));
```

### 7.6 Tablonun konumu Roslyn'den, EF'ten değil

EF tablonun **adını** bilir, **satırını** bilmez. `table:cart.carts` node'unun
`CartConfiguration.cs:10`'u göstermesi ayrı bir Roslyn taramasından geliyor: solution'daki her
`ToTable("literal")` çağrısı `(modül, tablo adı)` ile indeksleniyor
(`DataLayerOverlay.cs:678-727`), ve migration'lar **bilerek** dışarıda tutuluyor:

```csharp
// FlowLens: src/FlowLens.Core/DataLayerOverlay.cs:729-745
    /// <summary>
    /// EF's generated model code: migrations, their designers, and the model snapshot. All three
    /// contain a full ToTable inventory, and none of them is where a name is decided.
    /// </summary>
    private static bool IsGeneratedModelCode(string? filePath)
    {
        ...
        return normalized.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase);
    }
```

Gerekçe: `filePath` + `line`'ın tüm vaadi oraya gidip tarif edileni **değiştirebilmek**. Üretilmiş
bir snapshot'a yönlendirmek okuyucuyu düzenlememesi gereken bir dosyaya gönderir. (Bu hata bir kez
yapıldı: `cart.carts` bir dönem `20260717152814_InitialCartSchema.Designer.cs:39`'u gösteriyordu.)

Modül anahtarın parçası çünkü bu repoda hem `catalog.outbox_messages` hem `ordering.outbox_messages`
var — çıplak literal belirsiz.

### 7.7 Bu akışın ürettiği veri kenarlarının tamamı

`graph.json`'dan, birebir:

| Kaynak | Hedef | Kind | Mekanizma | Evidence |
|---|---|---|---|---|
| `…GetAsync` | `entity:…CartRecord` | Reads | `DbSetProperty` | `context.Carts at …:14` |
| `…RemoveAsync` | `entity:…CartRecord` | Reads | `DbSetProperty` | `context.Carts at …:75` |
| `…RemoveAsync` | `entity:…CartRecord` | Writes | `DbSetProperty` | `context.Carts.Remove at …:80` |
| `…SaveAsync` | `entity:…CartRecord` | Reads | `DbSetProperty` | `context.Carts at …:44` |
| `…SaveAsync` | `entity:…CartRecord` | Writes | `DbSetProperty` | `context.Carts.Add at …:49` |
| `…SaveAsync` | `entity:…CartRecord` | Writes | `OwnedCollectionAdd` | `context.Carts.Add(...) at …:49` |
| `…SaveAsync` | `entity:…CartRecord` | Writes | **`EntityConstruction`** | `new CartRecord(...) at …:49` |
| `…SaveAsync` | `entity:…CartItemRecord` | Writes | **`EntityConstruction`** | `new CartItemRecord(...) at …:41` |
| `…SaveAsync` | `column:cart.carts.CustomerId` | Writes | `PropertyAssignment` | `CustomerId = ... at …:51` |
| `…SaveAsync` | `column:cart.carts.Items` | Writes | `PropertyAssignment` | `Items = ... at …:52` |
| `…SaveAsync` | `column:cart.carts.UpdatedAtUtc` | Writes | `PropertyAssignment` | `UpdatedAtUtc = ... at …:53` |
| üç kolon | `table:cart.carts` | MapsTo | `EfModelMapping` | `EF Core model: CartRecord.X -> cart.carts` |
| iki entity | `table:cart.carts` | MapsTo | `EfModelMapping` | `EF Core model via CartDbContext` |

Üç şey:

1. **Hiçbir metottan doğrudan `table:cart.carts`'a kenar yok.** Tabloya her zaman bir Column ya da
   Entity üzerinden, bir sıçrama sonra ulaşılır.
2. **`SaveAsync → entity:CartRecord` dört ayrı Writes kenarı.** Tekilleştirme
   `(from, to, kind, **mechanism**)` üzerinden yapılıyor (`DataLayerOverlay.cs:804`) — yani
   **mekanizma kenarın kimliğinin parçası**, süsü değil. Aynı iddia dört farklı gerekçeyle
   ayrı ayrı duruyor ve her biri denetlenebilir.
3. **`EntityConstruction` ikinci sınıf kanıt.** Doğru olabilir ama doğrudan okunmuş değildir; graph
   bunu `mechanism` alanıyla ayırt edilebilir tutuyor ve akış dokümanı `second-class-evidence`
   başlığıyla ilan ediyor.

`cart.carts`'ın **üç** kolon node'u var: `CustomerId`, `Items`, `UpdatedAtUtc`. `xmin` gibi bir
satır sürümü yok (olsa `IsRowVersion` filtresine takılırdı, `DataLayerOverlay.cs:187`), ve owned
`CartItemRecord`'un üyeleri hiç kolon değil.

---

## 8. Node id'ler ve `callSite`

### 8.1 Dört biçim

```csharp
// FlowLens: src/FlowLens.Core/NodeId.cs:35-40
    public const string EndpointPrefix = "endpoint:";
    public const string EventPrefix = "event:";
    public const string EntityPrefix = "entity:";
    public const string TablePrefix = "table:";
    public const string ColumnPrefix = "column:";
    public const string ExternalPrefix = "external:";
```

| Tür | Üretim | Bu akıştaki gerçek id |
|---|---|---|
| Endpoint | `NodeId.cs:68-69` | `endpoint:DELETE /api/cart/items/{productId:guid}` |
| Metot | `NodeId.cs:57-58` — **öneksiz** | `ModularCommerce.Cart.Infrastructure.Persistence.PostgresCartRepository.SaveAsync(ModularCommerce.Cart.Domain.Carts.Cart, System.Threading.CancellationToken)` |
| Entity | `NodeId.cs:85` | `entity:ModularCommerce.Cart.Infrastructure.Persistence.CartRecord` |
| Tablo | `NodeId.cs:94-95` | `table:cart.carts` |
| Kolon | `NodeId.cs:97-98` | `column:cart.carts.Items` |

Metot id'sinin biçimi:

```csharp
// FlowLens: src/FlowLens.Core/NodeId.cs:20-28
    public static readonly SymbolDisplayFormat MemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
```

**Parametreler opsiyonel değil:** `ProductChangedConsumer` yalnız `ConsumeContext<T>` argümanıyla
ayrılan iki `Consume` overload'u taşıyor. Parametresiz id onları tek node'a çökertir ve iki ayrı
`CONSUMES` kenarı birleşirdi.

**Şema da opsiyonel değil:** bu repoda hem `catalog.outbox_messages` hem `ordering.outbox_messages`
var; şemasız `table:` id'si ikisini tek node yapardı.

`DisplayName` (`NodeId.cs:109-110`, `"PostgresCartRepository.SaveAsync"`) **asla kimlik anahtarı
değildir** — yalnız insan için kısa etiket.

### 8.2 `callSite` — 0-tabanlıdan 1-tabanlıya, tek yerde

```csharp
// FlowLens: src/FlowLens.Core/SourceLocation.cs:21-29
    public static (string FilePath, int Line, int Column) WithColumn(SyntaxNode node, string solutionDirectory)
    {
        var location = node.GetLocation();
        var (path, line) = FromLocation(location, solutionDirectory);

        return line == 0
            ? (path, 0, 0)
            : (path, line, location.GetLineSpan().StartLinePosition.Character + 1);
    }
```

```csharp
// FlowLens: src/FlowLens.Core/SourceLocation.cs:54-63
        var span = location.GetLineSpan();
        var path = span.Path;

        if (string.IsNullOrEmpty(path))
        {
            return (NoSource, 0);
        }

        var relative = Path.GetRelativePath(solutionDirectory, path).Replace('\\', '/');
        return (relative, span.StartLinePosition.Line + 1);
```

Zincir: `SyntaxNode.GetLocation()` → `Location.GetLineSpan()` → `FileLinePositionSpan.StartLinePosition`
(bir `LinePosition`, `.Line` ve `.Character` taşır). Roslyn **iki eksende de 0-tabanlı**; FlowLens
her ikisine `+1` ekliyor ve bu **tek bir yerde** oluyor. `line == 0` ise "kaynak yok" sentinel'i
(`NoSource = "(no source)"`) ve `GraphJson.Validate` bunu reddediyor.

**Doğrulama, elle:** graph.json endpoint→handler kenarı için `CartEndpoints.cs:63`, kolon 32 diyor.
O satır:

```
            var result = await handler.HandleAsync(user.GetUserId(), productId, cancellationToken);
```

12 boşluk + `var result = await ` (19 karakter) = `handler`'dan önce 31 karakter. Roslyn
`Character == 31`, dosyada `column: 32`. Aynı satırdaki `user.GetUserId()` ise kolon 52 —
**kolon olmasaydı bu iki çağrının sırası kaynağa değil bir tie-break'e kalırdı.**

### 8.3 Node'un satırı ile kenarın satırı farklı sorular

```csharp
// FlowLens: src/FlowLens.Core/GraphModel.cs:189-203
/// <summary>
/// Where a call is WRITTEN, which is not where the callee is declared - the node's own Line is the
/// declaration. Source order is the only order static analysis can honestly report: it says nothing
/// about what runs, or whether it runs at all.
/// </summary>
/// <param name="Column">
/// 1-based. Two calls can share a line (measured: Product.Create and Money.Create on
/// CreateProductHandler.cs:21) and without the column their order falls to a tie-break instead of
/// to the source.
/// </param>
/// <param name="Conditional">
/// The invocation sits inside a branch - a ternary, if/else, switch, or the short-circuiting side
/// of &amp;&amp;, || or ??. Such a step may not run at all. Measured because RemoveItemHandler's
/// two persistence calls are the two arms of one ternary and exclude each other.
/// </param>
```

Bu ayrım Faz 5'te ölçüldü: alfabetik sıralama vakaların **%61'inde** kaynak sırasından farklıydı ve
okuyucu soldan sağa okuyup kod sırası sanıyordu. Numaralı adımların **%19'u koşullu**. Bu akışta
üç adımın ikisi koşullu ve **birbirini dışlıyor**.

---

## 9. `graph.json`'a yazılma

```csharp
// FlowLens: src/FlowLens.Core/GraphJson.cs:76-87
    public static void Write(string path, GraphDocument document)
    {
        Validate(document.Nodes, document.Edges);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(Canonical(document), Options));
    }
```

**Önce doğrula, sonra yaz.** `Validate` (`:150-202`) boş `filePath`, `line <= 0`, kök/utility
çelişkisi ve dangling kenar arıyor; ihlalde `InvalidGraphException` fırlatılıyor ve dosya **hiç
yazılmıyor** (exit 5). Bozuk bir graph'ı teslim etmek hiç teslim etmemekten kötüdür.

```csharp
// FlowLens: src/FlowLens.Core/GraphJson.cs:57-74
    /// <summary>
    /// Only nulls are omitted - never default values.
    /// <para>
    /// This used to be <c>WhenWritingDefault</c>, which silently dropped every field whose value
    /// happened to be the enum's zero: 25 Endpoint nodes shipped with no <c>kind</c>, and all 512
    /// CALLS edges with no <c>kind</c> either. ...
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
```

Bu bir hata düzeltmesinin kalıntısı ve **bizim endpoint'imiz o hatanın tam ortasındaydı**:
`WhenWritingDefault` ile `NodeKind.Endpoint` (enum değeri 0) serialize edilmiyordu, yani 25
Endpoint node'u `kind` alanı **olmadan** yazılıyordu. Her tüketicinin *"alan yoksa Endpoint
demektir"* gibi yazılı olmayan bir kuralı bilmesi gerekirdi — hata değil, **güvenle yanlış okuma**.

Determinizm:

```csharp
// FlowLens: src/FlowLens.Core/GraphJson.cs:109-128
    public static GraphDocument Canonical(GraphDocument document) => document with
    {
        Nodes = [.. document.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal)],
        Edges =
        [
            .. document.Edges
                .OrderBy(e => e.FromId, StringComparer.Ordinal)
                .ThenBy(e => e.ToId, StringComparer.Ordinal)
                .ThenBy(e => e.Kind)
                .ThenBy(e => e.Mechanism)
                .ThenBy(e => e.Evidence ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.Ambiguous)
                // Call sites arrive in walk order, which is source order within one body; the key
                // keeps the ordering total once two edges agree on everything above.
                .ThenBy(e => e.FirstCallSite?.FilePath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.FirstCallSite?.Line ?? 0)
                .ThenBy(e => e.FirstCallSite?.Column ?? 0),
        ],
        Diagnostics = [.. document.Diagnostics.OrderBy(d => d, StringComparer.Ordinal)],
    };
```

Sıralama anahtarı **tam**: `Mechanism` de anahtara dahil olduğu için §7.7'deki dört
`SaveAsync → CartRecord` kenarı her koşuda aynı sırada çıkıyor. Gerekçe ölçülmüş — değişmemiş
kaynağın iki build'i küme olarak özdeş dosyalar üretiyordu ama 8 node ve 40 kenar yer değiştirmişti;
sebebi yukarıda, `SymbolFinder.FindImplementationsAsync`'in sıra vaat etmemesi.

Zaman damgası **yapısal olarak** yok:

```csharp
// FlowLens: src/FlowLens.Core/GraphJson.cs:22-29
public sealed record GraphStats(
    IReadOnlyDictionary<string, int> NodesByType,
    IReadOnlyDictionary<string, int> EdgesByType,
    IReadOnlyDictionary<string, int> EdgesByMechanism,
    int AmbiguousNodes,
    int UtilityNodes,
    int RootCount,
    [property: JsonIgnore] long ElapsedMs);
```

Kendi build süresini gömen bir artefakt iki koşuda byte-identical olamaz — ve `graph.json`'ın diff'i
okunabilir olmalı, çünkü Faz 3'ün dört gerçek hatasını bulan şey tam olarak **dosyayı elle
okumaktı**.

---

## Zincirin tamamı, tek bakışta

```
ModularCommerce.sln
  │  MSBuildWorkspace.OpenSolutionAsync
  ▼
Solution (66 proje)
  │  DescendantNodes<InvocationExpressionSyntax> + GetSymbolInfo + MapVerbs
  ▼
MapCallSite { HttpMethod="DELETE", RouteSuffix="/items/{productId:guid}",
              HandlerBody=<lambda>, Origin=(Parameter,"/") }        CartEndpoints.cs:57
  │  RoutePrefixResolver BFS + RouteText.Combine(prefix, origin, suffix)
  ▼
endpoint:DELETE /api/cart/items/{productId:guid}                    depth 0, rootKind Endpoint
  │  CALLS  CartEndpoints.cs:63:32
  ▼
RemoveItemHandler.HandleAsync(Guid, Guid, CancellationToken)        depth 1, kind Handler
  │  CALLS  RemoveItemHandler.cs:34:21  (KOŞULLU — ternary'nin bir dalı)
  ▼
ICartRepository.SaveAsync(Cart, CancellationToken)                  depth 2, kind Repository
  │  CALLS  (çağrı yeri yok — DI çözümlemesi), ambiguous
  ├──────────────► CachingCartRepository.SaveAsync                  depth 3  ──┐
  ▼                                                                            │ inner.SaveAsync
PostgresCartRepository.SaveAsync                                    depth 3  ◄─┘ (2-cycle)
  │  WRITES / PropertyAssignment   evidence: …Repository.cs:51,52,53
  ▼
column:cart.carts.{CustomerId, Items, UpdatedAtUtc}                 CartRecord.cs:4,5,6
  │  MAPS_TO / EfModelMapping
  ▼
table:cart.carts                                                    CartConfiguration.cs:10
  │  GraphJson.Validate → Canonical → JsonSerializer.Serialize
  ▼
graph.json
```

Diğer dal (`cart.IsEmpty` doğruysa, `RemoveItemHandler.cs:33`) `RemoveAsync` üzerinden gidiyor ve
**hiçbir kolon** yazmıyor — `Remove`, `WholeRowMethods` içinde değil.

---

## Bu akışın ilan ettiği sınırlar

Doküman üreteci bunları `out/flows/delete-api-cart-items-productid-guid.md`'de ayrıca basıyor. Hiçbiri
gizlenmiyor:

| Kod | Bu akışta ne demek |
|---|---|
| `ambiguous-implementation` | `ICartRepository`'nin iki implementasyonu da graph'ta; graph **hangisinin** koştuğunu kaydetmiyor. Decorator zincirinde ikisi de koşar, yani burada doğru cevap. (L3/L11) |
| `second-class-evidence` | `new CartRecord(...)` ve `new CartItemRecord(...)` — entity inşasından çıkarılmış yazma iddiaları. Doğru olabilir, doğrudan okunmuş değil. |
| `unmapped-column` | `CartItemRecord.{ProductId, Quantity, AddedAtUtc}` — jsonb belgesinin içinde yaşıyorlar, kendi kolonları yok. (L16-1 / L23 ailesi) |
| **L17** | `RedisCartCache` hiçbir node üretmiyor; `ExternalCall` yalnız `HttpClient` çağrılarını tanıyor. |

---

## Kullanılan Roslyn API'leri — tek tablo

Bu zincirde geçen her API, ne döndürdüğü ve FlowLens'te çağrıldığı satır. Satırı doğrulanamayan
hiçbir API bu tabloda yok.

| API | Ne döndürür | Nerede çağrılıyor |
|---|---|---|
| `MSBuildLocator.RegisterDefaults()` *(MSBuild, Roslyn değil)* | `void`; SDK'nın `Microsoft.Build.*`'ını gösteren `AssemblyResolve` kancasını kurar | `FlowLens.Cli/Program.cs:16` |
| `MSBuildWorkspace.Create()` | `MSBuildWorkspace` | `SolutionLoader.cs:47` |
| `Workspace.RegisterWorkspaceFailedHandler(…)` | `IDisposable` kayıt; obsolete `WorkspaceFailed` olayının yerine geçer | `SolutionLoader.cs:54` |
| `MSBuildWorkspace.OpenSolutionAsync(…)` | `Solution`; **yüklenemeyen proje için fırlatmaz** | `SolutionLoader.cs:60` |
| `Solution.Projects` / `Project.SupportsCompilation` | Proje listesi ve derlenebilirlik bayrağı | `GraphBuilder.cs:74-76` |
| `Project.Documents` | Projenin `Document` listesi | `DataLayerOverlay.cs:687` |
| `Document.GetSyntaxRootAsync(ct)` | `SyntaxNode?` — dosyanın sözdizimi ağacının kökü | `EndpointDiscovery.cs:55`, `DataLayerOverlay.cs:700` |
| `Document.GetSemanticModelAsync(ct)` | `SemanticModel?`; **workspace tarafından cache'lenir** | `EndpointDiscovery.cs:68`, `CallGraphWalker.cs:581`, `DataLayerOverlay.cs:790` |
| `Solution.GetDocument(SyntaxTree)` | Ağaca karşılık gelen `Document?` | `CallGraphWalker.cs:573`, `DataLayerOverlay.cs:784` |
| `Project.GetCompilationAsync(ct)` | `Compilation?` | `GraphBuilder.cs:222`, `DataLayerOverlay.cs:360,758` |
| `SyntaxNode.DescendantNodes().OfType<InvocationExpressionSyntax>()` | Gövdedeki tüm çağrı ifadeleri | `EndpointDiscovery.cs:113`, `CallGraphWalker.cs:238`, `DataLayerOverlay.cs:706` |
| `SyntaxNode.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()` | `new X(...)` ve `new(...)` ifadeleri | `EntityAccessAnalyzer.cs:239` |
| `SyntaxNode.Ancestors()` | Kökten aşağı ata düğümler; koşullu-dal ve ctor tespiti | `PropertyWriteAnalyzer.cs:215`, `CallGraphWalker.cs:332-364` |
| `SemanticModel.GetSymbolInfo(node, ct)` | `SymbolInfo` — `Symbol` (tek aday bağlandıysa) + `CandidateSymbols` + `CandidateReason` | `EndpointDiscovery.cs:123,225,255`, `CallGraphWalker.cs:251` |
| `SemanticModel.GetTypeInfo(expr, ct)` | `TypeInfo` — ifadenin statik tipi. `SaveChangesAsync`'te **alıcının** tipi | `EntityAccessAnalyzer.cs:123,155,203,243`, `PropertyWriteAnalyzer.cs:173`, `EndpointDiscovery.cs:320` |
| `SemanticModel.GetDeclaredSymbol(node, ct)` | Bildirim düğümünün sembolü | `PropertyWriteAnalyzer.cs:218`, `EntityAccessAnalyzer.cs:324,334` |
| `SemanticModel.GetConstantValue(expr, ct)` | `Optional<object?>` — derleme zamanı sabiti; `const string Route` de çözülür | `EndpointDiscovery.cs:366` |
| `SemanticModel.GetEnclosingSymbol(position, ct)` | Konumu kapsayan sembol; extension method'da **unreduced** form | `EndpointDiscovery.cs:390` |
| `IMethodSymbol.ReducedFrom` | Reduced extension method'un `this`'li hâli, başka her şeyde `null` | `NodeId.cs:55` |
| `ISymbol.OriginalDefinition` | Generic örneklemeleri açık tanıma çökertir | `NodeId.cs:55,61`, `GraphBuilder.cs:262,270` |
| `ISymbol.ToDisplayString(SymbolDisplayFormat)` | Node id'nin kendisi | `NodeId.cs:58,61` |
| `SymbolDisplayFormat` (ctor) | Id biçimini sabitler — parametreler dahil | `NodeId.cs:20-28` |
| `ISymbol.DeclaringSyntaxReferences` + `.GetSyntax()` | Sembolden **geri sözdizimine**; metadata sembolleri için boş | `CallGraphWalker.cs:475-477`, `GraphBuilder.cs:203-205`, `DataLayerOverlay.cs:298-300` |
| `SymbolFinder.FindImplementationsAsync(member, solution, scope, ct)` | Arayüz **üyesini** implement eden üyeler; **sıra vaat etmez** | `ImplementationResolver.cs:88` |
| `Compilation.GetTypeByMetadataName(name)` | Metadata adından `INamedTypeSymbol?` | `GraphBuilder.cs:228-229`, `DataLayerOverlay.cs:362,762-763` |
| `INamedTypeSymbol.ConstructedFrom` / `.MetadataName` | `DbSet\`1` eşlemesi — display string'e güvenmeden | `EntityAccessAnalyzer.cs:496-498` |
| `SymbolEqualityComparer.Default` | Sembol eşitliği — **compilation içinde** güvenilir | `PropertyWriteAnalyzer.cs:219`, `GraphBuilder.cs:262,270`, `RoutePrefixResolver.cs:40-41` |
| `ISymbol.Locations` / `Location.IsInSource` | Framework/NuGet sembollerini eleyen filtre | `SourceLocation.cs:41,45` |
| `SyntaxNode.GetLocation()` → `Location.GetLineSpan()` → `.StartLinePosition` | `LinePosition` — **0-tabanlı** `.Line` ve `.Character`; `+1` yalnız burada | `SourceLocation.cs:23,28,54,63` |
