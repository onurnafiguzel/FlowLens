<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# POST /api/identity/refresh

**Modül:** Identity · **Tanım:** `src/Modules/Identity/ModularCommerce.Identity.Api/Endpoints/AuthEndpoints.cs:42`

```mermaid
flowchart TD
  n0["Identity · RefreshHandler.HandleAsync"]
  n1["Identity · RefreshTokenRepository.Add"]
  n2["Identity · RefreshTokenRepository.GetByTokenHashAsync"]
  n3["Identity · UserRepository.GetByIdAsync"]
  n4[["Identity · POST /api/identity/refresh"]]
  n5[("Identity · identity.refresh_tokens")]
  n6[("Identity · identity.users")]

  n0 --> n1
  n0 --> n2
  n0 --> n3
  n0 ==> n5
  n1 ==> n5
  n2 ==>|"RefreshToken"| n5
  n3 ==>|"User"| n6
  n4 --> n0
```


## Veri katmanı

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `identity.refresh_tokens` | WR | `CreatedAtUtc`, `ExpiresAtUtc`, `Id`, `RevokedAtUtc`, `TokenHash`, `UserId` | `src/Modules/Identity/ModularCommerce.Identity.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs:11` |
| `identity.users` | R | — | `src/Modules/Identity/ModularCommerce.Identity.Infrastructure/Persistence/Configurations/UserConfiguration.cs:11` |

## Diyagram neyi göstermiyor

Gösterilen **7** node; ham yürüyüş **34** node'a ulaşıyor. Gizlenen: 17 ara çağrı, 5 utility, 4 arayüz bildirimi, 1 veriye ulaşmayan dal.

Tam liste: `flowlens trace "POST /api/identity/refresh"`

## Bilinen sınırlar

- **second-class-evidence** — Bazi yazma iddialari dolayli kanita dayaniyor: entity insasi ya da parametreli bir SaveChanges. Dogru olabilir, ama dogrudan okunmus degil.<br>`new RefreshToken(...) at src/Modules/Identity/ModularCommerce.Identity.Domain/Users/RefreshToken.cs:42`

> Diyagram dar görünüyorsa tıklayarak büyütebilir veya
> [mermaid.live'da açabilirsiniz](https://mermaid.live/edit#pako:eAEBVQKq_XsiY29kZSI6ImZsb3djaGFydCBURFxuICBuMFtcIklkZW50aXR5IMK3IFJlZnJlc2hIYW5kbGVyLkhhbmRsZUFzeW5jXCJdXG4gIG4xW1wiSWRlbnRpdHkgwrcgUmVmcmVzaFRva2VuUmVwb3NpdG9yeS5BZGRcIl1cbiAgbjJbXCJJZGVudGl0eSDCtyBSZWZyZXNoVG9rZW5SZXBvc2l0b3J5LkdldEJ5VG9rZW5IYXNoQXN5bmNcIl1cbiAgbjNbXCJJZGVudGl0eSDCtyBVc2VyUmVwb3NpdG9yeS5HZXRCeUlkQXN5bmNcIl1cbiAgbjRbW1wiSWRlbnRpdHkgwrcgUE9TVCAvYXBpL2lkZW50aXR5L3JlZnJlc2hcIl1dXG4gIG41WyhcIklkZW50aXR5IMK3IGlkZW50aXR5LnJlZnJlc2hfdG9rZW5zXCIpXVxuICBuNlsoXCJJZGVudGl0eSDCtyBpZGVudGl0eS51c2Vyc1wiKV1cblxuICBuMCAtLT4gbjFcbiAgbjAgLS0-IG4yXG4gIG4wIC0tPiBuM1xuICBuMCA9PT4gbjVcbiAgbjEgPT0-IG41XG4gIG4yID09PnxcIlJlZnJlc2hUb2tlblwifCBuNVxuICBuMyA9PT58XCJVc2VyXCJ8IG42XG4gIG40IC0tPiBuMFxuIiwibWVybWFpZCI6IntcbiAgXCJ0aGVtZVwiOiBcImRlZmF1bHRcIlxufSIsImF1dG9TeW5jIjp0cnVlLCJ1cGRhdGVEaWFncmFtIjp0cnVlfRybzOo).
