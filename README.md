# con_dms_locker_cmd.dll — Build & Usage Guide

## โครงสร้างไฟล์

```
ConDmsLockerCmd/
├── ConDmsLockerCmd.csproj   ← Class Library project
├── LockerCommands.cs        ← Public API (3 exported functions)
└── LockerController.cs      ← RS-485 protocol implementation

LockerDllWrapper.cs          ← ใส่ใน WPF project
ConnectViewModel.cs          ← ตัวอย่าง ViewModel สำหรับหน้า Connect
```

---

## วิธี Build DLL

```bash
cd ConDmsLockerCmd
dotnet build -c Release
```

Output: `bin/Release/net8.0-windows/con_dms_locker_cmd.dll`

---

## วิธีใช้งาน (Option A — แนะนำ)

เพิ่ม ProjectReference ใน WPF `.csproj`:

```xml
<ProjectReference Include="..\ConDmsLockerCmd\ConDmsLockerCmd.csproj"/>
```

แล้วใช้ผ่าน `LockerService`:

```csharp
// 1. Connect
bool ok = LockerService.ConnectPort("COM3");

// 2. Check ว่าช่องล็อกอยู่ไหม
bool locked = LockerService.CheckLocked(boardAddr: 0x01, lockAddr: 0x05);

// 3. Unlock
string result = LockerService.Unlock(boardAddr: 0x01, lockAddr: 0x05);
if (result == "ok") { /* สำเร็จ */ }
else { /* result = "ex-error: ..." */ }
```

---

## API Reference

### `cmdConnectPort(portName)`
| | |
|---|---|
| Input | `portName` — ชื่อ port เช่น `"COM3"` |
| Return | `true` = เชื่อมต่อสำเร็จ, `false` = failed |

### `cmdCheckLocked(boardAddr, lockAddr)`
| | |
|---|---|
| Input | `boardAddr` 0x01–0x20, `lockAddr` 0x01–0x18 |
| Return | `true` = ล็อกอยู่ (ประตูปิด), `false` = เปิดอยู่ |

### `cmdUnlock(boardAddr, lockAddr)`
| | |
|---|---|
| Input | `boardAddr` 0x01–0x20, `lockAddr` 0x01–0x18 |
| Return | `"ok"` = เปิดสำเร็จ, `"ex-error: <message>"` = failed |

---

## Protocol Bytes (RS-485)

| คำสั่ง | Packet |
|---|---|
| Unlock | `8A [board] [lock] 11 [BCC]` |
| Check status | `80 [board] [lock] 33 [BCC]` |
| Read all (board) | `80 [board] 00 33 [BCC]` |
| Unlock multi | `90 [board] [S1] [S2] [S3] [BCC]` |

BCC = XOR ของทุก byte ก่อน BCC

---

## Serial Settings
- Baud: 9600
- Data: 8 bits
- Parity: None
- Stop: 1 bit
- Response timeout: 600 ms

---

## Important Rule (จาก spec)
> ห้ามเปิดทั้ง 2 ด้าน (Pharmacy + Delivery) พร้อมกัน

ควรเพิ่ม lock ใน business logic layer ก่อนเรียก `cmdUnlock`
