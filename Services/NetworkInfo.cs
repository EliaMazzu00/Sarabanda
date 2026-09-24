using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sarabanda.Services;

/// <summary>
/// Trova gli indirizzi con cui questo PC è raggiungibile dagli altri dispositivi della
/// rete locale, così il menu può suggerirli invece di costringere l'utente a cercarli
/// con <c>ipconfig</c>.
/// </summary>
/// <remarks>
/// Il punto delicato è che su un PC normale ci sono più schede di rete, e diverse sono
/// virtuali: VirtualBox, Hyper-V, VPN. Hanno indirizzi privati perfettamente validi
/// (<c>192.168.56.1</c> e simili) su cui però un telefono collegato al Wi-Fi di casa non
/// arriverà mai. Per questo gli indirizzi vengono <b>ordinati</b>, non presi a caso:
/// prima quelli delle schede che hanno un <b>gateway</b> configurato — cioè quelle che
/// portano davvero a un router — poi Wi-Fi ed Ethernet, e solo per ultimo il resto.
/// </remarks>
public static class NetworkInfo
{
    /// <summary>
    /// Gli indirizzi IPv4 su cui questo PC può essere raggiunto, dal più probabile al
    /// meno probabile. Di solito il primo è quello del Wi-Fi o del cavo di rete.
    /// </summary>
    public static IReadOnlyList<string> GetLanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsableInterface)
                .Select(nic => new
                {
                    Nic = nic,
                    Properties = nic.GetIPProperties()
                })
                .OrderByDescending(x => HasGateway(x.Properties))   // porta a un router
                .ThenByDescending(x => IsPhysicalKind(x.Nic))       // Wi-Fi o Ethernet
                .SelectMany(x => x.Properties.UnicastAddresses)
                .Select(address => address.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            // Se il sistema non espone le schede di rete si rinuncia al suggerimento:
            // non è un errore che debba fermare l'applicazione.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Scarta le schede spente, il loopback e i tunnel: quelle non servono a
    /// raggiungere un dispositivo nella stessa stanza.
    /// </summary>
    private static bool IsUsableInterface(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up &&
        nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);

    /// <summary>
    /// Vero se la scheda ha un gateway IPv4 valido. È il segnale che distingue la rete
    /// di casa dalle schede virtuali, che di gateway non ne hanno.
    /// </summary>
    private static bool HasGateway(IPInterfaceProperties properties) =>
        properties.GatewayAddresses.Any(gateway =>
            gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
            !gateway.Address.Equals(IPAddress.Any));

    /// <summary>Vero per le schede Wi-Fi ed Ethernet, quelle fisiche più comuni.</summary>
    private static bool IsPhysicalKind(NetworkInterface nic) =>
        nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet;
}
