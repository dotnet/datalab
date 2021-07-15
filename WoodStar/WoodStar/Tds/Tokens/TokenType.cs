namespace WoodStar;

public enum TokenType : byte
{
    TVP_ROW = 0x01,
    RETURNSTATUS = 0x79,
    COLMETADATA = 0x81,
    ALTMETADATA = 0x88,
    DATACLASSIFICATION = 0xA3,
    TABNAME = 0xA4,
    COLINFO = 0xA5,
    ORDER = 0xA9,
    ERROR = 0xAA,
    INFO = 0xAB,
    RETURNVALUE = 0xAC,
    LOGINACK = 0xAD,
    FEATUREEXTACK = 0xAE,
    ROW = 0xD1,
    NBCROW = 0xD2,
    ALTROW = 0xD3,
    ENVCHANGE = 0xE3,
    SESSIONSTATE = 0xE4,
    SSPI = 0xED,
    FEDAUTHINFO = 0xEE,
    DONE = 0xFD,
    DONEPROC = 0xFE,
    DONEINPROC = 0xFF,
    // OFFSET - removed in 7.2
}
