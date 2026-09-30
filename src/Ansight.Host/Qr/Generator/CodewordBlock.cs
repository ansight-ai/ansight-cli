using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal struct CodewordBlock
{
    public CodewordBlock(int groupNumber, int blockNumber, string bitString, List<string> codeWords,
        List<string> eccWords, List<int> codeWordsInt, List<int> eccWordsInt)
    {
        this.GroupNumber = groupNumber;
        this.BlockNumber = blockNumber;
        this.BitString = bitString;
        this.CodeWords = codeWords;
        this.ECCWords = eccWords;
        this.CodeWordsInt = codeWordsInt;
        this.ECCWordsInt = eccWordsInt;
    }

    public int GroupNumber { get; }
    public int BlockNumber { get; }
    public string BitString { get; }
    public List<string> CodeWords { get; }
    public List<int> CodeWordsInt { get; }
    public List<string> ECCWords { get; }
    public List<int> ECCWordsInt { get; }
}
