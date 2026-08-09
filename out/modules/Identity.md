<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Identity

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `POST /api/identity/login` | 2 | `src/Modules/Identity/ModularCommerce.Identity.Api/Endpoints/AuthEndpoints.cs:33` | [diyagram](../flows/post-api-identity-login.md) |
| `POST /api/identity/logout` | 1 | `src/Modules/Identity/ModularCommerce.Identity.Api/Endpoints/AuthEndpoints.cs:52` | [diyagram](../flows/post-api-identity-logout.md) |
| `POST /api/identity/refresh` | 2 | `src/Modules/Identity/ModularCommerce.Identity.Api/Endpoints/AuthEndpoints.cs:42` | [diyagram](../flows/post-api-identity-refresh.md) |
| `POST /api/identity/signup` | 1 | `src/Modules/Identity/ModularCommerce.Identity.Api/Endpoints/AuthEndpoints.cs:20` | [diyagram](../flows/post-api-identity-signup.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `identity.refresh_tokens` | WR | `CreatedAtUtc`, `ExpiresAtUtc`, `Id`, `RevokedAtUtc`, `TokenHash`, `UserId` | `src/Modules/Identity/ModularCommerce.Identity.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs:11` |
| `identity.users` | WR | `CreatedAtUtc`, `Id`, `PasswordHash`, `email` | `src/Modules/Identity/ModularCommerce.Identity.Infrastructure/Persistence/Configurations/UserConfiguration.cs:11` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

Bu modül integration event tanımlamıyor.

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

yok.

**Bu modüle dokunanlar:**

yok.

## Bilinen sınırlar

Bu sayfa için kaydedilmiş bir sınır yok.
