namespace NLightning.Client.Utils;

public static class ClientUtils
{
    public static void ShowUsage()
    {
        Console.WriteLine("NLightning Node Client");
        Console.WriteLine("Usage:");
        Console.WriteLine("  nltg [options] [command]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --network, -n <network>    Network to use (mainnet, testnet, regtest, signet, mutinynet) [default: mainnet]");
        Console.WriteLine("  --cookie, -c <path>        Path to cookie file");
        Console.WriteLine("  --help, -h, -?             Show this help message");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  info                         Get node information via IPC");
        Console.WriteLine("  connect <node>               Connect to a peer node");
        Console.WriteLine("  listpeers                    List all connected peers");
        Console.WriteLine("  listchannels [peer_id]       List channels, optionally only those with one peer");
        Console.WriteLine("  getaddress [p2tr|p2wpkh|all] Gets an unused address of the requested type [default: p2tr]");
        Console.WriteLine("  walletbalance                Gets the wallet balance");
        Console.WriteLine("  openchannel <node> <sats> [push_sats]");
        Console.WriteLine("                               Open a channel to peer, optionally giving it push_sats");
        Console.WriteLine("  createinvoice <msat|any> [description] [expiry_seconds]");
        Console.WriteLine("                               Create an invoice (alias: addinvoice)");
        Console.WriteLine("  payinvoice <bolt11> [msat] [timeout_seconds] [--max-fee-msat <msat>] [--max-parts <n>]");
        Console.WriteLine("             [--timeout <seconds>]");
        Console.WriteLine("                               Pay an invoice and wait for the outcome (alias: pay); the");
        Console.WriteLine("                               amount is only for invoices without one; exit code 1 if it");
        Console.WriteLine("                               failed; timeout 1-300 s [default: 60], also the end of the");
        Console.WriteLine("                               retries, then it stays in flight and listpayments shows the");
        Console.WriteLine("                               outcome; fee limit [default: max(0.5%, 5000 msat)]; parts:");
        Console.WriteLine("                               HTLCs in flight at once, 1-128, 1 never splits [default: 16]");
        Console.WriteLine("  closechannel <channel_id> [feerate_per_kw|0] [wait_seconds] [nofeerange]");
        Console.WriteLine("                               Cooperatively close a channel (alias: close-channel) and");
        Console.WriteLine("                               wait for the closing transaction [wait 0-300 s, default 30];");
        Console.WriteLine("                               nofeerange negotiates without fee_range");
        Console.WriteLine("  forceclosechannel <channel_id>");
        Console.WriteLine("                               Fail a channel and broadcast our latest commitment (alias:");
        Console.WriteLine("                               force-close-channel)");
        Console.WriteLine("  pendingsweeps [channel_id] [all]");
        Console.WriteLine("                               Show the on-chain resolution of closed channels (alias:");
        Console.WriteLine("                               pending-sweeps); all includes the closed ones");
        Console.WriteLine("  chainstatus                  Show whether chain processing is halted and what is refused");
        Console.WriteLine("                               meanwhile (alias: chain-status); exit code 1 if halted");
        Console.WriteLine("  listinvoices [count] [skip]  List invoices, newest first [count 1-1000, default 100]");
        Console.WriteLine("  listpayments [count] [skip]  List outgoing payments, newest first [count 1-1000,");
        Console.WriteLine("                               default 100]");
        Console.WriteLine();
        Console.WriteLine("Environment Variables:");
        Console.WriteLine("  NLTG_NETWORK               Network to use");
        Console.WriteLine("  NLTG_COOKIE                Path to cookie file");
        Console.WriteLine();
        Console.WriteLine("Cookie file location: ~/.nltg/{network}/nltg.cookie");
    }
}