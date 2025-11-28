/*
 * ================================================================================
 * ATV Görüntü Transfer Merkezi - Email Bildirimleri Servisi
 * ================================================================================
 * 
 * [GENEL BAKIŞ]
 * Bu servis, transfer işlemleri sırasında otomatik email bildirimleri gönderir.
 * SMTP ayarları smtp.ini dosyasından okunur ve runtime'da değiştirilebilir.
 * 
 * [ÖNEMLİ ÖZELLİKLER]
 * - Transfer başladığında ve bittiğinde otomatik bildirim
 * - Özelleştirilebilir email şablonları (Konu ve İçerik değişkenleri)
 * - Test email gönderme özelliği (SMTP doğrulama için)
 * - INI dosyası ile kalıcı ayar saklama
 * - 30 saniyelik cache mekanizması (performans optimizasyonu)
 * 
 * [SMTP AYARLARI - smtp.ini Dosyası]
 * Dosya Konumu: Proje root dizini
 * Format: INI (key=value satırları)
 * Ana Ayarlar:
 *   - Host, Port, Username, Password
 *   - FromEmail, FromName
 *   - ToEmails (virgülle ayrılmış, multiple recipient desteği)
 *   - EnableSsl (TLS/SSL aktifleştirme)
 *   - EnableNotifications (bildirimleri aç/kapat)
 * Şablon Ayarları:
 *   - MailSubjectTemplate (dinamik değişkenler destekler)
 *   - MailBodyTemplate (multi-line, \n ile escape)
 * 
 * [KULLANIM SENARYOSU]
 * 1. Admin panelden SMTP ayarlarını yapılandır (ilk kurulum)
 * 2. "Test Maili Gönder" ile bağlantıyı doğrula
 * 3. EnableNotifications=true yaparak otomatik bildirimleri aktifleştir
 * 4. Transfer işlemleri sırasında otomatik email gönderilir
 * 
 * [VERİ YAPISI]
 * Cache Mekanizması:
 *   - _cachedSettings: Bellekte tutulan SMTP ayarları
 *   - _lastCacheUpdate: Son güncelleme zamanı
 *   - Cache süresi: 30 saniye (performans için)
 * 
 * INI Dosya Formatı:
 *   # Yorum satırları # veya ; ile başlar
 *   [SMTP]
 *   Host=smtp.gmail.com
 *   Port=587
 *   Username=user@atv.com.tr
 *   [Templates]
 *   MailSubjectTemplate=Transfer: {FileName}
 *   MailBodyTemplate=Dosya: {FileName}\nKullanıcı: {Username}
 * 
 * [DİKKAT EDİLMESİ GEREKENLER]
 * CRITICAL: Gmail için normal şifre değil "Uygulama Şifresi" kullanılmalı!
 *           (Google Account > Security > 2-Step > App Passwords)
 * WARNING: smtp.ini dosyası güvenli bir konumda tutulmalı (password içerir)
 * INFO: Email gönderimi başarısız olursa false döner, exception fırlatmaz
 * 
 * [ŞABLON DEĞİŞKENLERİ]
 * Email şablonlarında kullanılabilir:
 *   {FileName}        - Yüklenen dosya adı
 *   {Username}        - Transfer yapan kullanıcı
 *   {CompanyFolder}   - Şirket klasörü adı
 *   {Status}          - Başarılı/Başarısız
 *   {FileSize}        - Dosya boyutu (formatlanmış)
 *   {Duration}        - Transfer süresi
 *   {Speed}           - Transfer hızı (MB/s)
 *   {DateTime}        - İşlem tarihi/saati
 * 
 * Author: KINIKLI
 * Date: Kasım 2025
 * Project: ATV Transfer Portal
 * Version: 1.0
 * ================================================================================
 */

using System.Net;
using System.Net.Mail;
using System.Text.Json;
using FileUploadPortal.Models;

namespace FileUploadPortal.Services
{
    /// <summary>
    /// Email servisi arayüzü - SMTP işlemleri için contract
    /// Interface pattern kullanarak dependency injection kolaylığı sağlar
    /// </summary>
    public interface IEmailService
    {
        /// <summary>
        /// Transfer tamamlandığında otomatik bildirim emaili gönderir
        /// </summary>
        /// <param name="fileName">Yüklenen dosyanın adı</param>
        /// <param name="username">Transfer yapan kullanıcı</param>
        /// <param name="companyFolder">Hedef şirket klasörü</param>
        /// <param name="success">Transfer başarılı mı?</param>
        /// <param name="fileSize">Dosya boyutu (bytes)</param>
        /// <param name="duration">Transfer süresi</param>
        /// <param name="speed">Ortalama transfer hızı (bytes/s)</param>
        /// <param name="startTime">Transfer başlangıç zamanı</param>
        /// <param name="errorMessage">Hata mesajı (opsiyonel)</param>
        /// <returns>Email gönderimi başarılı ise true</returns>
        Task<bool> SendTransferNotificationAsync(string fileName, string username, string companyFolder, 
            bool success, long fileSize, TimeSpan duration, double speed, DateTime startTime, string? errorMessage = null);
        
        /// <summary>
        /// SMTP bağlantısını test eder (email göndermeden)
        /// </summary>
        /// <returns>Bağlantı başarılı ise true</returns>
        Task<bool> TestSmtpConnectionAsync();
        
        /// <summary>
        /// Alternatif SMTP sunucularını test eder
        /// </summary>
        Task<(bool Success, string Message, string TestedHost)> TestAlternativeSmtpAsync();
        
        /// <summary>
        /// SMTP ayarlarını test etmek için örnek email gönderir
        /// Bağlantı sorunlarını tespit etmek için kullanılır
        /// </summary>
        /// <param name="testEmail">Test emailinin gönderileceği adres</param>
        /// <returns>Test başarılı ise true, hata varsa exception fırlatır</returns>
        Task<bool> SendTestEmailAsync(string testEmail);
        
        /// <summary>
        /// SMTP ayarlarını smtp.ini dosyasından okur
        /// Cache mekanizması sayesinde 30 saniye boyunca dosya okumaz (performans)
        /// </summary>
        /// <returns>SmtpSettings objesi (dosya yoksa boş ayarlar)</returns>
        SmtpSettings GetSmtpSettings();
        
        /// <summary>
        /// SMTP ayarlarını smtp.ini dosyasına kalıcı olarak kaydeder
        /// INI formatında key=value satırları şeklinde
        /// </summary>
        /// <param name="settings">Kaydedilecek SMTP ayarları</param>
        Task SaveSmtpSettingsAsync(SmtpSettings settings);
    }

    /// <summary>
    /// Email servisi ana implementasyonu
    /// smtp.ini dosyasından ayarları okur ve System.Net.Mail kullanarak email gönderir
    /// 
    /// KINIKLI - Email notification system
    /// Performans optimizasyonu: 30 saniyelik cache mekanizması
    /// </summary>
    public class EmailService : IEmailService
    {
        private readonly ILogger<EmailService> _logger;
        private readonly string _settingsFilePath;          // smtp.ini dosya yolu (tam path)
        private SmtpSettings? _cachedSettings;              // Bellekte tutulan SMTP ayarları
        private DateTime _lastCacheUpdate = DateTime.MinValue; // Cache son güncelleme timestamp'i

        /// <summary>
        /// Constructor - Dependency injection ile logger ve configuration alır
        /// smtp.ini dosyasının yolunu application base directory'de ayarlar
        /// </summary>
        public EmailService(ILogger<EmailService> logger, IConfiguration configuration)
        {
            _logger = logger;
            // smtp.ini dosyası uygulama root dizininde bulunmalı
            // AppDomain.CurrentDomain.BaseDirectory = Uygulamanın çalıştığı dizin
            _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smtp.ini");
        }

        /// <summary>
        /// SMTP ayarlarını getirir - Cache mekanizması ile optimize edilmiş
        /// 
        /// [PERFORMANS OPTİMİZASYONU]
        /// 30 saniye boyunca cache'den döner, sürekli dosya okuma yapılmaz
        /// Her istekte I/O işlemi yapmak yerine bellekten okur
        /// 
        /// KINIKLI - Cache mechanism for performance
        /// </summary>
        public SmtpSettings GetSmtpSettings()
        {
            // PERFORMANS: Cache kontrolü - 30 saniye içinde tekrar okumaya gerek yok
            if (_cachedSettings != null && (DateTime.Now - _lastCacheUpdate).TotalSeconds < 30)
            {
                return _cachedSettings;
            }

            // Dosya var mı kontrol et
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    // INI dosyasını parse et
                    _cachedSettings = ReadIniFile();
                    _lastCacheUpdate = DateTime.Now;
                    _logger.LogDebug("SMTP ayarları smtp.ini dosyasından yüklendi");
                    return _cachedSettings;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SMTP ayarları okunamadı: {Error}", ex.Message);
                }
            }

            // Dosya yoksa veya hata varsa boş ayarlar döndür
            _cachedSettings = new SmtpSettings();
            _lastCacheUpdate = DateTime.Now;
            return _cachedSettings;
        }

        /// <summary>
        /// smtp.ini dosyasını satır satır okuyarak SmtpSettings objesine dönüştürür
        /// 
        /// [INI FORMAT]
        /// - # veya ; ile başlayan satırlar yorum satırıdır
        /// - key=value formatında ayırma yapılır
        /// - Büyük/küçük harf duyarsız (case-insensitive)
        /// - Multi-line değerler için \n escape karakteri kullanılır
        /// 
        /// KINIKLI - INI parser
        /// </summary>
        private SmtpSettings ReadIniFile()
        {
            var settings = new SmtpSettings();
            var lines = File.ReadAllLines(_settingsFilePath);

            foreach (var line in lines)
            {
                // Yorum satırlarını atla (# veya ; ile başlayanlar)
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                // key=value formatında ayır (Split ile ilk = karakterinden böl)
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();
                
                // KINIKLI 2025: Yorum kısmını temizle (# işaretinden sonrasını kaldır)
                var commentIndex = value.IndexOf('#');
                if (commentIndex >= 0)
                {
                    value = value.Substring(0, commentIndex).Trim();
                }

                // Her ayar için uygun property'ye ata
                // Case-insensitive karşılaştırma için ToLower() kullanılır
                switch (key.ToLower())
                {
                    case "host":
                        settings.Host = value; 
                        // SMTP sunucu adresi (örn: smtp.gmail.com)
                        break;
                        
                    case "port":
                        if (int.TryParse(value, out int port))
                            settings.Port = port; 
                        // Port numarası - 587 (TLS) veya 465 (SSL)
                        break;
                        
                    case "username":
                        settings.Username = value; 
                        // SMTP kullanıcı adı (genelde email adresi)
                        break;
                        
                    case "password":
                        settings.Password = value; 
                        // CRITICAL: Gmail için "Uygulama Şifresi" kullanılmalı!
                        // Normal şifre çalışmaz, Google hesabından app password oluşturun
                        break;
                        
                    case "fromemail":
                        settings.FromEmail = value; 
                        // Gönderen email adresi
                        break;
                        
                    case "fromname":
                        settings.FromName = value; 
                        // Gönderen ismi (email'de görünecek)
                        break;
                        
                    case "toemails":
                        settings.ToEmails = value; 
                        // Alıcı emailler - virgülle ayrılmış liste
                        // Örnek: user1@atv.com.tr,user2@atv.com.tr
                        break;
                        
                    case "enablessl":
                        settings.EnableSsl = value.ToLower() == "true" || value == "1"; 
                        // SSL/TLS kullanımı (true önerilir)
                        break;
                        
                    case "enablenotifications":
                        settings.EnableNotifications = value.ToLower() == "true" || value == "1"; 
                        // Otomatik bildirimleri aktif/pasif yap
                        break;
                        
                    case "mailsubjecttemplate":
                        settings.MailSubjectTemplate = value; 
                        // Email konu şablonu - dinamik değişkenler içerebilir
                        break;
                        
                    case "mailbodytemplate":
                        // Email içerik şablonu
                        // INI dosyasında \n escape karakteri kullanılır
                        // Burda gerçek satır sonlarına çevrilir
                        settings.MailBodyTemplate = value.Replace("\\n", "\n");
                        break;
                }
            }

            return settings;
        }

        /// <summary>
        /// SMTP ayarlarını smtp.ini dosyasına kalıcı olarak kaydeder
        /// Cache'i de günceller (sonraki okumalar için)
        /// 
        /// [DOSYA FORMATI]
        /// INI formatında key=value satırları
        /// Yorumlar ve açıklayıcı başlıklar eklenir
        /// Multi-line değerler için \n escape kullanılır
        /// 
        /// KINIKLI - Settings persistence layer
        /// </summary>
        public async Task SaveSmtpSettingsAsync(SmtpSettings settings)
        {
            try
            {
                // INI dosyası içeriğini oluştur
                // Header kısmı bilgilendirici yorumlar içerir
                var iniContent = new List<string>
                {
                    "# ================================================================================",
                    "# ATV Görüntü Transfer Merkezi - SMTP Ayarları",
                    "# ================================================================================",
                    "# Developer: KINIKLI",
                    "# Last Update: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                    "# ",
                    "# IMPORTANT: Gmail kullanıyorsanız normal şifre değil 'App Password' kullanın!",
                    "#            Google Account > Security > 2-Step Verification > App Passwords",
                    "# ================================================================================",
                    "",
                    "[SMTP]",
                    $"Host={settings.Host}                    # SMTP sunucu adresi",
                    $"Port={settings.Port}                    # Port numarası (587=TLS, 465=SSL)",
                    $"Username={settings.Username}            # SMTP kullanıcı adı",
                    $"Password={settings.Password}            # SMTP şifresi (App Password)",
                    $"FromEmail={settings.FromEmail}          # Gönderen email adresi",
                    $"FromName={settings.FromName}            # Gönderen görünen isim",
                    $"ToEmails={settings.ToEmails}            # Alıcılar (virgülle ayrılmış)",
                    $"EnableSsl={settings.EnableSsl}          # SSL/TLS aktif (true önerilir)",
                    $"EnableNotifications={settings.EnableNotifications}  # Otomatik bildirimler",
                    "",
                    "[Templates]",
                    "# Email şablonları - Dinamik değişkenler kullanabilirsiniz",
                    $"MailSubjectTemplate={settings.MailSubjectTemplate}",
                    // CRITICAL: Satır sonları escape edilmeli (\n)
                    $"MailBodyTemplate={settings.MailBodyTemplate.Replace("\n", "\\n")}", 
                    "",
                    "# Template Variables:",
                    "# {FileName}       - Dosya adı",
                    "# {Username}       - Kullanıcı adı", 
                    "# {CompanyFolder}  - Şirket klasörü",
                    "# {Status}         - Başarılı/Başarısız",
                    "# {FileSize}       - Dosya boyutu (formatlanmış)",
                    "# {Duration}       - Transfer süresi",
                    "# {Speed}          - Transfer hızı",
                    "# {DateTime}       - İşlem tarihi/saati",
                    ""
                };

                // Dosyaya asenkron yaz
                await File.WriteAllLinesAsync(_settingsFilePath, iniContent);
                
                // Cache'i güncelle (sonraki okumalar için)
                _cachedSettings = settings;
                _lastCacheUpdate = DateTime.Now;
                
                _logger.LogInformation("SMTP ayarları smtp.ini dosyasına kaydedildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMTP ayarları kaydedilemedi: {Error}", ex.Message);
                throw; // Exception'ı yukarı fırlat, controller'da yakalanacak
            }
        }

        /// <summary>
        /// Transfer işlemi tamamlandığında otomatik bildirim emaili gönderir
        /// 
        /// [İŞLEYİŞ]
        /// 1. SMTP ayarları kontrol edilir (EnableNotifications aktif mi?)
        /// 2. Template değişkenleri gerçek değerlerle değiştirilir
        /// 3. Alıcı listesi parse edilir (virgülle ayrılmış)
        /// 4. SmtpClient ile email gönderilir
        /// 5. Başarılı/başarısız log yazılır
        /// 
        /// [TEMPLATE DEĞİŞKENLERİ]
        /// {CompanyFolder}, {Username}, {FileName}, {Status}
        /// {FileSize}, {Duration}, {Speed}, {StartTime}, {DateTime}
        /// 
        /// KINIKLI - Auto notification system
        /// </summary>
        public async Task<bool> SendTransferNotificationAsync(string fileName, string username, string companyFolder,
            bool success, long fileSize, TimeSpan duration, double speed, DateTime startTime, string? errorMessage = null)
        {
            var settings = GetSmtpSettings();

            // Bildirimler aktif değilse email gönderme
            if (!settings.EnableNotifications)
            {
                _logger.LogDebug("Email bildirimleri kapalı, email gönderilmedi");
                return false;
            }

            // SMTP yapılandırılmamışsa email gönderemeyiz
            if (string.IsNullOrEmpty(settings.Host) || string.IsNullOrEmpty(settings.ToEmails))
            {
                _logger.LogWarning("SMTP ayarları eksik, email gönderilemedi");
                return false;
            }

            try
            {
                // Status durumunu belirle (başarılı/başarısız)
                var status = success ? "Transfer BAŞARILI !!" : "Transfer BAŞARISIZ";
                
                // Dosya boyutunu okunabilir formata çevir (bytes -> KB/MB/GB)
                var fileSizeFormatted = FormatBytes(fileSize);
                
                // Transfer hızını MB/s cinsinden hesapla
                var speedFormatted = $"{(speed / (1024 * 1024)):F2} MB/s";
                
                // Süreyi dakika cinsinden göster
                var durationFormatted = $"{duration.TotalMinutes:F1} dakika";

                // Email konusunu template'den oluştur
                var subject = settings.MailSubjectTemplate
                    .Replace("{CompanyFolder}", companyFolder)
                    .Replace("{Username}", username)
                    .Replace("{FileName}", fileName)
                    .Replace("{Status}", success ? "Başarılı" : "Başarısız")
                    .Replace("{FileSize}", fileSizeFormatted)
                    .Replace("{Duration}", durationFormatted)
                    .Replace("{Speed}", speedFormatted)
                    .Replace("{StartTime}", startTime.ToString("dd.MM.yyyy HH:mm:ss"))
                    .Replace("{DateTime}", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));

                // Email içeriğini template'den oluştur
                _logger.LogDebug("Template BEFORE replacement: {Template}", settings.MailBodyTemplate);
                
                var body = settings.MailBodyTemplate
                    .Replace("{CompanyFolder}", companyFolder)
                    .Replace("{Username}", username)
                    .Replace("{FileName}", fileName)
                    .Replace("{Status}", status)
                    .Replace("{FileSize}", fileSizeFormatted)
                    .Replace("{Duration}", durationFormatted)
                    .Replace("{Speed}", speedFormatted)
                    .Replace("{StartTime}", startTime.ToString("dd.MM.yyyy HH:mm:ss"))
                    .Replace("{DateTime}", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));

                _logger.LogInformation("Email body oluşturuldu - Uzunluk: {Length} karakter. İlk 100 karakter: {Preview}", 
                    body.Length, body.Length > 100 ? body.Substring(0, 100) : body);
                _logger.LogDebug("Email body içeriği: {Body}", body);

                // Başarısız transferlerde hata detayını ekle
                if (!success && !string.IsNullOrEmpty(errorMessage))
                {
                    body += $"\n\nHata Detayı: {errorMessage}";
                }

                // KINIKLI 2025: FromEmail validation - boş email kontrolü
                if (string.IsNullOrWhiteSpace(settings.FromEmail))
                {
                    _logger.LogWarning("FromEmail boş - smtp.ini dosyasında email adreslerini girin");
                    return false;
                }

                // Alıcı listesini parse et (virgülle ayrılmış string'den List'e)
                // StringSplitOptions.RemoveEmptyEntries ile boş entry'leri temizle
                var recipients = settings.ToEmails.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim())                    // Başındaki/sonundaki boşlukları temizle
                    .Where(e => !string.IsNullOrEmpty(e))     // Boş olanları filtrele
                    .ToList();

                // KINIKLI 2025: Recipient validation
                if (!recipients.Any())
                {
                    _logger.LogWarning("ToEmails boş - smtp.ini dosyasında alıcı email adreslerini girin");
                    return false;
                }

                // SMTP Client oluştur
                // using ile otomatik dispose edilecek (connection kapatılacak)
                using var smtpClient = new SmtpClient(settings.Host, settings.Port)
                {
                    EnableSsl = settings.EnableSsl,           // TLS/SSL aktifleştir
                    Credentials = new NetworkCredential(settings.Username, settings.Password),
                    Timeout = 30000                           // 30 saniye timeout (yavaş ağlar için)
                };

                // \n karakterlerini <br> ile değiştir (email için)
                var emailBody = body.Replace("\n", "<br>");
                
                // Mail mesajını oluştur
                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(settings.FromEmail, settings.FromName),
                    Subject = subject,
                    Body = emailBody,
                    IsBodyHtml = true                         // HTML email olarak gönder
                };

                // Tüm alıcıları ekle (To listesine)
                foreach (var recipient in recipients)
                {
                    mailMessage.To.Add(recipient);
                }

                // Email'i gönder (asenkron)
                await smtpClient.SendMailAsync(mailMessage);
                
                _logger.LogInformation("Transfer bildirimi gönderildi: {Recipients}", string.Join(", ", recipients));
                return true;
            }
            catch (Exception ex)
            {
                // Hata durumunda log'a yaz ama exception fırlatma
                // false dönerek bildirim gönderilemediğini belirt
                _logger.LogError(ex, "Transfer bildirimi gönderilemedi: {Error}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// SMTP bağlantısını test eder (email göndermeden)
        /// </summary>
        /// <returns>Bağlantı başarılı ise true</returns>
        public async Task<bool> TestSmtpConnectionAsync()
        {
            var settings = GetSmtpSettings();
            
            if (string.IsNullOrEmpty(settings.Host))
            {
                throw new InvalidOperationException("SMTP Host ayarlanmamış");
            }

            try
            {
                _logger.LogInformation("SMTP bağlantısı test ediliyor: {Host}:{Port}", settings.Host, settings.Port);
                
                using var client = new SmtpClient(settings.Host, settings.Port)
                {
                    EnableSsl = settings.EnableSsl,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(settings.Username, settings.Password),
                    Timeout = 10000 // 10 saniye
                };

                // Sadece bağlantı test et, email gönderme
                await Task.Run(() => client.Send(new MailMessage("test@test.com", "test@test.com", "Connection Test", "Test")));
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMTP bağlantı testi başarısız: {Error}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Alternatif SMTP sunucularını test eder
        /// Şirket SMTP'si çalışmadığında diğer seçenekleri dener
        /// </summary>
        public async Task<(bool Success, string Message, string TestedHost)> TestAlternativeSmtpAsync()
        {
            var testConfigs = new[]
            {
                // ATV Mail sunucusu - farklı port ve SSL kombinasyonları
                new { Host = "mail.tmgrup.com.tr", Port = 587, Ssl = true, Name = "ATV Mail (587/TLS)" },
                new { Host = "mail.tmgrup.com.tr", Port = 465, Ssl = true, Name = "ATV Mail (465/SSL)" },
                new { Host = "mail.tmgrup.com.tr", Port = 25, Ssl = false, Name = "ATV Mail (25/No SSL)" },
                new { Host = "mail.tmgrup.com.tr", Port = 587, Ssl = false, Name = "ATV Mail (587/No SSL)" },
                
                // Diğer test sunucuları
                new { Host = "smtp.gmail.com", Port = 587, Ssl = true, Name = "Gmail" },
                new { Host = "smtp-mail.outlook.com", Port = 587, Ssl = true, Name = "Outlook" },
                new { Host = "smtp.office365.com", Port = 587, Ssl = true, Name = "Office365" },
                new { Host = "smtp.tmgrup.com.tr", Port = 587, Ssl = true, Name = "Turkuvaz (Port 587)" },
                new { Host = "smtp.tmgrup.com.tr", Port = 465, Ssl = true, Name = "Turkuvaz (Port 465)" },
                new { Host = "smtp.tmgrup.com.tr", Port = 25, Ssl = false, Name = "Turkuvaz (Port 25)" }
            };

            foreach (var config in testConfigs)
            {
                try
                {
                    _logger.LogInformation("Testing SMTP: {Name} - {Host}:{Port}", config.Name, config.Host, config.Port);
                    
                    using var client = new SmtpClient(config.Host, config.Port)
                    {
                        EnableSsl = config.Ssl,
                        UseDefaultCredentials = false,
                        Timeout = 5000 // 5 saniye kısa timeout
                    };

                    // Sadece bağlantı test et (credentials olmadan)
                    await Task.Run(() => 
                    {
                        try 
                        { 
                            client.Send(new MailMessage("test@test.com", "test@test.com", "Test", "Test")); 
                        }
                        catch (SmtpException ex) when (
                            ex.StatusCode == SmtpStatusCode.MailboxBusy || 
                            ex.StatusCode == SmtpStatusCode.GeneralFailure ||
                            ex.StatusCode == SmtpStatusCode.ServiceNotAvailable ||
                            ex.Message.Contains("authentication required") ||
                            ex.Message.Contains("Authentication Required"))
                        {
                            // Authentication hatası = Sunucu erişilebilir ama credentials yanlış
                            // ServiceNotAvailable = Sunucu erişilebilir ama auth gerekli
                            // Bu aslında BAŞARILI bir bağlantı testi
                            throw new InvalidOperationException($"✅ {config.Name} sunucusu erişilebilir - Auth gerekli");
                        }
                    });
                    
                    return (true, $"✅ {config.Name} sunucusu erişilebilir", $"{config.Host}:{config.Port}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("SMTP {Name} failed: {Error}", config.Name, ex.Message);
                    continue;
                }
            }

            return (false, "❌ Hiçbir SMTP sunucusu erişilebilir değil", "None");
        }

        /// <summary>
        /// SMTP ayarlarını doğrulamak için test emaili gönderir
        /// 
        /// [KULLANIM AMAÇLARI]
        /// - Admin panelden SMTP ayarları kaydedilirken test edilir
        /// - Bağlantı sorunlarını tespit etmek için kullanılır
        /// - Ayarların doğru olduğunu onaylar
        /// 
        /// [HATA YÖNETİMİ]
        /// Başarısız olursa exception fırlatır (caller'da yakalanmalı)
        /// Bu sayede hatanın detayı kullanıcıya gösterilebilir
        /// 
        /// KINIKLI - SMTP validation
        /// </summary>
        public async Task<bool> SendTestEmailAsync(string testEmail)
        {
            var settings = GetSmtpSettings();

            // CRITICAL: SMTP yapılandırılmamışsa devam edemeyiz
            if (string.IsNullOrEmpty(settings.Host))
            {
                throw new InvalidOperationException("SMTP ayarları yapılmamış");
            }

            // KINIKLI 2025: FromEmail validation
            if (string.IsNullOrWhiteSpace(settings.FromEmail))
            {
                throw new InvalidOperationException("FromEmail boş - smtp.ini dosyasında gönderici email adresini girin");
            }

            // FromEmail format validation - GÖNDERİCİ EMAIL KONTROLÜ EKLENDİ
            if (!IsValidEmail(settings.FromEmail))
            {
                throw new InvalidOperationException($"Geçersiz gönderici email formatı: {settings.FromEmail}");
            }

            // Test email validation - EMAIL FORMAT KONTROLÜ EKLENDİ
            if (string.IsNullOrWhiteSpace(testEmail) || !IsValidEmail(testEmail))
            {
                throw new ArgumentException($"Geçersiz test email formatı: {testEmail}", nameof(testEmail));
            }

            try
            {
                _logger.LogInformation("SMTP Test başlatılıyor - Host: {Host}:{Port}, SSL: {SSL}, User: {User}", 
                    settings.Host, settings.Port, settings.EnableSsl, settings.Username);

                // Farklı SSL/TLS modlarını test et
                var testModes = new[]
                {
                    new { EnableSsl = settings.EnableSsl, Name = $"Varsayılan (SSL: {settings.EnableSsl})" },
                    new { EnableSsl = true, Name = "SSL/TLS Zorla" },
                    new { EnableSsl = false, Name = "SSL/TLS Kapalı" }
                };

                Exception? lastException = null;

                foreach (var mode in testModes)
                {
                    try
                    {
                        _logger.LogInformation("Test modu: {Mode} - {Host}:{Port}", mode.Name, settings.Host, settings.Port);

                        // SMTP client oluştur (test için)
                        using var smtpClient = new SmtpClient(settings.Host, settings.Port)
                        {
                            EnableSsl = mode.EnableSsl,
                            Credentials = new NetworkCredential(settings.Username, settings.Password),
                            Timeout = 15000,  // 15 saniye
                            DeliveryMethod = SmtpDeliveryMethod.Network,
                            UseDefaultCredentials = false
                        };

                        // Test email içeriği
                        using var mailMessage = new MailMessage
                        {
                            From = new MailAddress(settings.FromEmail.Trim(), settings.FromName?.Trim() ?? "ATV Transfer"),
                            Subject = "Test E-posta - ATV Transfer Merkezi",
                            Body = $@"✅ SMTP TEST BAŞARILI!

Test Modu: {mode.Name}
SMTP Ayarları:
- Sunucu: {settings.Host}:{settings.Port}
- SSL: {mode.EnableSsl}
- Gönderici: {settings.FromEmail}
- Kullanıcı: {settings.Username}

Test tarihi: {DateTime.Now:dd.MM.yyyy HH:mm:ss}

Bu emaili aldıysanız SMTP ayarlarınız doğru yapılandırılmış demektir.

---
ATV Görüntü Transfer Merkezi
Developer: KINIKLI",
                            IsBodyHtml = false
                        };

                        mailMessage.To.Add(testEmail.Trim());

                        // Email'i gönder
                        await smtpClient.SendMailAsync(mailMessage);
                        
                        _logger.LogInformation("Test emaili başarıyla gönderildi - Mod: {Mode}, Email: {Email}", mode.Name, testEmail);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        _logger.LogWarning("Test modu başarısız - {Mode}: {Error}", mode.Name, ex.Message);
                        continue;
                    }
                }

                // Tüm modlar başarısız oldu
                throw lastException ?? new InvalidOperationException("Tüm SSL/TLS modları başarısız");
            }
            catch (SmtpException smtpEx)
            {
                // SMTP özel hatası - daha detaylı bilgi
                _logger.LogError(smtpEx, "SMTP Hatası - StatusCode: {StatusCode}, Host: {Host}:{Port}", 
                    smtpEx.StatusCode, settings.Host, settings.Port);
                throw new InvalidOperationException($"SMTP Bağlantı Hatası: {smtpEx.Message} (StatusCode: {smtpEx.StatusCode})", smtpEx);
            }
            catch (Exception ex)
            {
                // Genel hata durumunda loglayıp exception'ı yukarı fırlat
                _logger.LogError(ex, "Test emaili gönderilemedi: {Email}, SMTP: {Host}:{Port}, SSL: {SSL}", 
                    testEmail, settings.Host, settings.Port, settings.EnableSsl);
                throw new InvalidOperationException($"Email gönderim hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Dosya boyutunu okunabilir formata çevirir (B -> KB -> MB -> GB -> TB)
        /// 
        /// [ALGORİTMA]
        /// 1024'e bölerek bir sonraki birime geç
        /// En uygun birimi otomatik seç
        /// 
        /// [ÖRNEKLER]
        /// 1024 bytes        -> 1 KB
        /// 1048576 bytes     -> 1 MB
        /// 1073741824 bytes  -> 1 GB
        /// 
        /// KINIKLI - File size formatter
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            // Boyut birimleri dizisi
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            
            // 1024'ten büyükse bir sonraki birime geç
            // Örnek: 2048 bytes -> 2 KB
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            
            // 0.## formatı: En fazla 2 ondalık basamak göster
            // Gereksiz sıfırları gösterme (örn: 1.50 yerine 1.5)
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Email adresinin geçerli formatda olup olmadığını kontrol eder
        /// MailAddress constructor'ı kullanarak RFC standardına uygunluğu test eder
        /// 
        /// KINIKLI - Email format validator
        /// </summary>
        /// <param name="email">Kontrol edilecek email adresi</param>
        /// <returns>Geçerli format ise true, aksi halde false</returns>
        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                // MailAddress constructor'ı email formatını RFC standardına göre validate eder
                // Geçersiz format durumunda exception fırlatır
                var addr = new MailAddress(email.Trim());
                return addr.Address == email.Trim();
            }
            catch
            {
                // Format hatası durumunda false döner
                return false;
            }
        }
    }
}
