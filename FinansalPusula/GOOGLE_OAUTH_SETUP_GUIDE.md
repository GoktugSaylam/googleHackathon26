# Google OAuth Kurulum Rehberi (Server-Side BFF)

Bu projede Google OAuth code exchange artık **server tarafında** yapılıyor.
`client_secret` sadece backend'te tutuluyor; istemciye (Blazor WASM) hiç gönderilmiyor.

## 0) Tek Komutla Calistirma (Script)

Repo kokunde iki script var:

- `run-dev.ps1`
- `run-dev.cmd`

Kullanim:

```powershell
.\run-dev.ps1
```

veya:

```cmd
run-dev.cmd
```

Varsayilan davranis:
- Backend host calisir (`https://localhost:7015`)
- Frontend icin sadece build-watch calisir

Opsiyonel (frontend'i ayri host olarak da acmak):

```powershell
.\run-dev.ps1 -FrontendRunStandalone
```

## 1) Mimari Özeti

- İstemci proje: `FinansalPusula` (Blazor WebAssembly UI)
- Sunucu proje: `FinansalPusula.Server` (ASP.NET Core BFF + Google OAuth)
- Login endpointi: `/bff/login`
- Logout endpointi: `/bff/logout`
- Kullanıcı bilgisi endpointi: `/bff/user`
- Google callback endpointi (varsayılan): `/authentication/login-callback`

## 2) Google Cloud Console Ayarları

1. Google Cloud Console'da `OAuth client ID` oluştur.
2. Application type: `Web application`.
3. Aşağıdaki redirect URI mutlaka ekli olmalı:
  - `https://localhost:7015/authentication/login-callback`
4. Geriye donuk uyumluluk icin su callback'i de ekleyebilirsin:
  - `https://localhost:7015/signin-google`
5. Production için ayrıca kendi domain callback'ini ekle:
  - `https://app.senin-domainin.com/authentication/login-callback`
6. OAuth consent screen tarafında test kullanıcılarını tanımla.

Not:
- Bu mimaride varsayilan callback yolu `authentication/login-callback` olarak ayarlidir.
- Gerekiyorsa `FinansalPusula.Server/appsettings.json` icindeki `Authentication:Google:CallbackPath` degeri ile degistirilebilir.

## 3) Secret Yönetimi (Zorunlu)

`client_secret` kesinlikle git'e yazılmamalı.

Sunucu projesinde User Secrets kullan:

```powershell
dotnet user-secrets set --project .\FinansalPusula.Server\FinansalPusula.Server.csproj "Authentication:Google:ClientSecret" "BURAYA_GOOGLE_CLIENT_SECRET"
```

Gerekirse `ClientId` değerini de User Secrets'tan override edebilirsin:

```powershell
dotnet user-secrets set --project .\FinansalPusula.Server\FinansalPusula.Server.csproj "Authentication:Google:ClientId" "BURAYA_GOOGLE_CLIENT_ID.apps.googleusercontent.com"
```

## 4) Uygulamayı Çalıştırma

Sadece server projesini çalıştır:

```powershell
dotnet run --project .\FinansalPusula.Server\FinansalPusula.Server.csproj --launch-profile https
```

Uygulama adresi:
- `https://localhost:7015`

## 5) Doğrulama Checklist

1. Uygulamayı açınca login ekranı gelmeli.
2. `Google ile Giriş Yap` tıklanınca `/bff/login` üzerinden Google'a gitmeli.
3. Google dönüşü sonrası kullanıcı ana ekrana yönlenmeli.
4. Sağ üstte kullanıcı adı/e-posta görünmeli.
5. `Çıkış` ile oturum kapanmalı ve login ekranına dönmeli.

## 6) Sık Hatalar ve Kesin Nedenler

- `invalid_request: client_secret is missing`
  - Sunucuda `Authentication:Google:ClientSecret` tanımlı değil.
- `invalid_client` / `The provided client secret is invalid`
  - Google API key (AIza...) değeri `ClientSecret` değildir.
  - OAuth için Google Cloud'da `OAuth 2.0 Client IDs > Web application` altında verilen gerçek `Client secret` kullanılmalıdır.
- `redirect_uri_mismatch`
  - Google Console'da `https://localhost:7015/authentication/login-callback` eksik veya farklı.
- Login sonrası sürekli login ekranına düşme
  - Cookie yazılamıyor olabilir; HTTPS ve tarayıcı cookie politikalarını kontrol et.

## 7) Certbot Gerekli mi?

Lokal geliştirmede **gerekli değil**. Lokal için `dotnet dev-certs` kullanılır.

Certbot yalnızca production domain için gerekir. Örnek (Ubuntu + Nginx):

```bash
sudo apt update
sudo apt install certbot python3-certbot-nginx -y
sudo certbot --nginx -d app.senin-domainin.com
```

Sunucu ters proxy arkasındaysa bu projede `ForwardedHeaders` zaten etkinleştirildi.
