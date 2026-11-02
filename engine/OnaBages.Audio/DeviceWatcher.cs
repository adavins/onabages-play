using NAudio.CoreAudioApi;

namespace OnaBages.Audio;

/// <summary>
/// Vigila els dispositius d'àudio i avisa quan canvien (connexió, desconnexió,
/// canvi d'estat, de nom o de format).
///
/// Com funciona:
/// 1. Windows avisa de cada canvi, sovint amb diversos avisos seguits per a una sola acció.
/// 2. Cada avís només reinicia un temporitzador (dins de l'avís de Windows no es pot fer res lent).
/// 3. Quan fa <see cref="QuietPeriod"/> que no arriba cap avís, es torna a llegir la llista
///    i es compara amb l'anterior. Si hi ha diferències, es dispara <see cref="Changed"/>.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    /// <summary>Temps sense avisos nous abans de tornar a llegir la llista.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;
    private readonly TimeSpan _quietPeriod;
    private readonly Timer _timer;
    private readonly Lock _sync = new();
    private IReadOnlyList<AudioDevice> _last = [];
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private bool _disposed;

    /// <summary>
    /// Es dispara (en un fil del temporitzador) amb la llista de canvis.
    /// Els errors dels gestors d'aquest esdeveniment es capturen i s'envien a <see cref="Error"/>.
    /// </summary>
    public event Action<IReadOnlyList<DeviceChange>>? Changed;

    /// <summary>Es dispara si falla la lectura de dispositius o un gestor de <see cref="Changed"/>.</summary>
    public event Action<Exception>? Error;

    /// <param name="listDevices">Funció que llegeix els dispositius. Per defecte, els reals de Windows.</param>
    /// <param name="quietPeriod">Temps d'espera sense avisos. Per defecte, <see cref="QuietPeriod"/>.</param>
    public DeviceWatcher(Func<IReadOnlyList<AudioDevice>>? listDevices = null, TimeSpan? quietPeriod = null)
    {
        _listDevices = listDevices ?? DeviceList.GetAll;
        _quietPeriod = quietPeriod ?? QuietPeriod;
        _timer = new Timer(_ => CheckForChanges(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Llista de dispositius de l'última lectura.</summary>
    public IReadOnlyList<AudioDevice> Current
    {
        get { lock (_sync) return _last; }
    }

    /// <summary>
    /// Fa la primera lectura, que serà la referència per detectar canvis.
    /// <see cref="Start"/> ja la fa; les proves la criden directament per no dependre de Windows.
    /// </summary>
    public void ReadInitialList()
    {
        lock (_sync) _last = _listDevices();
    }

    /// <summary>
    /// Fa la primera lectura i comença a escoltar els avisos de Windows.
    /// </summary>
    public void Start()
    {
        ReadInitialList();

        _enumerator = new MMDeviceEnumerator();
        // false: els avisos arriben al fil d'àudio de Windows; només hi reiniciem el temporitzador.
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, _) => Poke();
        _notifications.DeviceRemoved += (_, _) => Poke();
        _notifications.DeviceStateChanged += (_, _) => Poke();
        _notifications.PropertyValueChanged += (_, _) => Poke();
    }

    /// <summary>
    /// Indica que alguna cosa pot haver canviat. Es pot cridar moltes vegades seguides:
    /// la lectura es fa una sola vegada, quan deixen d'arribar avisos.
    /// </summary>
    public void Poke()
    {
        try
        {
            _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // El vigilant s'està tancant: no cal fer res.
        }
    }

    private void CheckForChanges()
    {
        List<DeviceChange> changes;
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                var now = _listDevices();
                changes = DeviceChanges.Compare(_last, now);
                _last = now;
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            return;
        }

        if (changes.Count == 0) return;

        try
        {
            Changed?.Invoke(changes);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _notifications?.Dispose();
        _enumerator?.Dispose();
        _timer.Dispose();
    }
}
