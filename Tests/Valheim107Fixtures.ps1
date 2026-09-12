# Independent wire fixtures for Valheim 1.0.7. Do not rewrite game schema
# markers to make a fixture pass; the original DLL remains the API reference.
if (-not ('Valheim107Fixture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
public static class Valheim107Fixture
{
    public static int Hash(string name)
    {
        unchecked {
            int a = 5381, b = 5381;
            for (int i = 0; i < name.Length && name[i] != 0; i += 2) {
                a = ((a << 5) + a) ^ name[i];
                if (i + 1 == name.Length || name[i + 1] == 0) break;
                b = ((b << 5) + b) ^ name[i + 1];
            }
            return a + b * 1566083941;
        }
    }
    public static void Statistics(BinaryWriter writer)
    {
        writer.Write(205); writer.Write(10);
        for (int group = 0; group < 10; ++group) {
            for (int stat = 0; stat < 205; ++stat) writer.Write(0f);
            writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(5);
            for (int enemy = 0; enemy < 5; ++enemy) writer.Write(0);
            for (int table = 0; table < 5; ++table) writer.Write(0);
        }
    }
    public static void Item(BinaryWriter writer, string name, int stack, float durability,
        int x, int y, bool equipped, int quality, int variant, long crafter, string crafterName,
        byte[] customDictionary, int worldLevel, bool pickedUp, bool cheated)
    {
        // The custom dictionary argument is a test-friendly int-counted
        // dictionary. Only its count prefix is compacted for the item wire.
        int customCount = BitConverter.ToInt32(customDictionary, 0);
        writer.Write((int)(durability * 100f));
        writer.Write((byte)x); writer.Write((byte)y); writer.Write((byte)worldLevel);
        writer.Write((byte)((pickedUp ? 1 : 0) | (equipped ? 2 : 0) | 4 | 8 | 16 | 32 | 64 | 128));
        writer.Write((ushort)quality); writer.Write((ushort)stack); writer.Write(variant);
        writer.Write(crafter); writer.Write(crafterName);
        writer.Write(String.IsNullOrEmpty(name) ? 0 : Hash(name));
        if (customCount < 128) writer.Write((byte)customCount);
        else { writer.Write((byte)((customCount >> 8) | 128)); writer.Write((byte)customCount); }
        writer.Write(customDictionary, 4, customDictionary.Length - 4);
        writer.Write((byte)(cheated ? 1 : 0));
    }
}
'@
}
