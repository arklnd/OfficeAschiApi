# OfficeAschi

**Office seat booking system with TOTP-based authentication — full-stack app with an Angular PWA + .NET API.**

A lightweight system for teams to manage daily office seat bookings — with automatic waitlist promotion when seats free up, TOTP security with no passwords or sessions, and a mobile-ready client.

---

## Features

- **Create & manage teams** — anyone can create a team and become its manager
- **Seat management** — managers define seats (desks) available for booking
- **Daily seat booking** — approved members book a specific seat for a specific day
- **Automatic waitlist** — when all seats are full, members can waitlist for a desired seat; cancellations auto-promote the next person
- **TOTP authentication** — no passwords, no sessions, no tokens to store server-side; every elevated action is verified via a 6-digit TOTP code (Google Authenticator–style)
- **Client-side secret generation** — TOTP secrets are generated in the browser (using the `otpauth` library), shown as a QR code, and sent to the API only during creation/join for verification
- **Secrets stored in localStorage** — the Angular app stores TOTP secrets locally and auto-generates codes for API calls via an HTTP interceptor, so authenticated actions are seamless
- **Manual TOTP fallback** — if a secret isn't in localStorage (e.g. different browser), the app prompts for a 6-digit code from the user's authenticator app
- **Member approval workflow** — new members join in a "pending" state; the manager must approve them before they can book
- **Manager can deny or remove members** — deny pending requests or remove approved members (which cancels all their bookings and triggers waitlist promotions)
- **Team deletion** — managers can delete an entire team, cascading removal of all seats, members, and bookings
- **Seat deletion guards** — seats with existing bookings cannot be deleted until bookings are cancelled

- **Progressive Web App (PWA)** — installable, works offline with service worker support
- **Android native build** — Capacitor integration for native Android deployment
- **Backend health monitoring** — client polls `/health` and shows a banner when the backend is unreachable; auto-refreshes when it recovers
- **Dev menu** — development-only page for managing/implanting TOTP secrets in localStorage
- **Swagger UI** — full API documentation at `/swagger`

---

## Who Can Do What (and Why)

### Anyone (no auth)

| Action | Why |
|--------|-----|
| Search/list teams | Discovery — anyone can find a team to join |
| View team details | Check if a team exists, see if TOTP is set up |
| View seats in a team | See what desks are available before joining |
| View team members | See who's on the team |
| View booking availability for a date | Check who's coming to the office, which seats are free |
| Create a team (with TOTP setup) | No gatekeeping — creating a team also sets up TOTP, making the creator the manager |
| Join a team (with TOTP setup) | Anyone can request to join; TOTP is set up atomically so the member is ready once approved |

**Why no auth for viewing?** All read operations are public because the data isn't sensitive (team names, seat labels, who's sitting where). This keeps the system simple — no login walls just to check if your teammate is in the office.

**Why TOTP during creation/join?** Since there are no user accounts or passwords, TOTP is the only way to prove "I am the manager of this team" or "I am this member." Setting it up atomically during creation/join ensures every actor has an identity from the start — no half-created entities without auth, no extra setup steps to forget.

### Manager (requires `TOTP manager:{teamId}:{code}`)

| Action | Why auth is needed |
|--------|--------------------|
| Add seats to the team | Only the manager should define the physical workspace layout |
| Delete seats (only if no bookings exist) | Prevents accidental data loss — cancel bookings first |
| Approve pending members | Manager controls who gets access to book seats |
| Deny pending join requests | Reject unwanted/unknown join requests; removes the pending record |
| Remove approved members | Evicts a member; cancels all their bookings and triggers waitlist promotions for vacated seats |
| Delete the entire team | Nuclear option — removes team, all members, seats, and bookings |

**Why can't managers book seats?** Managers authenticate as `manager:{teamId}`, but booking requires `reportee:{reporteeId}` auth. A manager who wants to book must also join their own team as a reportee. This separation keeps the permission model clean.

### Reportee / Member (requires `TOTP reportee:{reporteeId}:{code}`)

| Action | Why auth is needed |
|--------|--------------------|
| Book a seat for a date | Prevents someone from booking on your behalf without your TOTP |
| Waitlist for a seat (when all full) | Same auth as booking — you're committing to come if a seat opens |
| Cancel your own booking | Only you can cancel your booking; cancellation triggers auto-promotion |

**Why can't a reportee book before approval?** The API explicitly checks `IsApproved`. This ensures the manager has vetted the person before they can claim limited seats.

**Why can't a reportee book if their specific seat is taken but others are free?** The API returns an error: _"This seat is taken. Other seats are available — pick a different one."_ Waitlisting only kicks in when **all** seats are full. This avoids the confusing scenario of someone waitlisting for a seat when empty desks are available.

**Why one booking per person per day?** A unique constraint (`Date + ReporteeId`) prevents double-booking. You get one seat per day — confirmed or waitlisted.

---

## Authentication: TOTP

Instead of traditional username/password auth, each actor (manager or reportee) has a **TOTP secret** (like Google Authenticator). Elevated API calls require a valid 6-digit TOTP code in the `Authorization` header:

```
Authorization: TOTP manager:{teamId}:{6-digit-code}
Authorization: TOTP reportee:{reporteeId}:{6-digit-code}
```

### How TOTP works in this system

1. **Secret generation happens in the client** — the Angular app generates a random 20-byte Base32 secret using the `otpauth` library (no server-side `/generate-secret` endpoint)
2. **QR code is shown** — the client renders a branded QR code (with logo) containing the `otpauth://` URI for the authenticator app
3. **User verifies** — the user scans the QR (or copies the secret), enters the 6-digit code from their authenticator
4. **Atomic creation** — the secret + code are sent to the API together (e.g. `POST /api/teams` or `POST /api/teams/{id}/reportees`). The server validates the code against the secret. If valid, the entity is created and the secret is stored. If invalid, nothing is created
5. **Client stores the secret** — on success, the secret is saved in `localStorage` (`totp_manager_{id}` or `totp_reportee_{id}`)
6. **Auto-auth via interceptor** — subsequent API calls that require TOTP are intercepted; the interceptor reads the secret from localStorage, generates a fresh code, and attaches the `Authorization` header automatically
7. **Fallback prompt** — if the secret isn't in localStorage (different device/browser), a dialog prompts the user to enter a 6-digit code manually from their authenticator app

### Why TOTP?

- **No passwords to leak** — secrets are stored per-entity in the DB and per-device in localStorage
- **No sessions to manage** — every request is independently verified
- **No token refresh flows** — TOTP codes are ephemeral (30-second window, ±1 step tolerance)
- **Works offline-ish** — authenticator apps generate codes without network access

---

## Waitlist Logic

When all seats in a team are booked for a day, a member can still book — but their booking is **waitlisted** for their desired seat.

**Auto-promotion on cancellation:**
1. When a confirmed booking is cancelled, first check if anyone is waitlisted **for that specific seat** → promote the earliest one
2. If no one is waitlisted for that seat, find the **globally earliest waitlisted person** in the team for that day → assign them the vacated seat (even if it wasn't their original preference)

**Auto-promotion on member removal:**
When a manager removes a member, all their bookings are cancelled. For each vacated confirmed seat, the waitlist promotion logic runs automatically.

**Example:**
> Team-101 has 3 seats (S1, S2, S3) and 15 members (M1–M15).
>
> **Confirmed:** S1←M1, S2←M2, S3←M3
> **Waitlisted for S1:** M4, M5, M6 (in order)
>
> - M1 cancels → M4 automatically gets S1 (earliest waitlisted for S1)
> - M3 cancels S3 → M5 gets S3 (next globally earliest waitlisted, reassigned from S1 waitlist to S3)

---

## API Endpoints

### Teams

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/teams?q={search}` | — | Search/list teams |
| `GET` | `/api/teams/{id}` | — | Get team details |
| `POST` | `/api/teams` | — | Create a team (with TOTP setup in one step) |
| `DELETE` | `/api/teams/{id}` | Manager TOTP | Delete team + all members, seats, bookings |

### Seats

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/teams/{teamId}/seats` | — | List seats in a team |
| `POST` | `/api/teams/{teamId}/seats` | Manager TOTP | Add a seat |
| `DELETE` | `/api/teams/{teamId}/seats/{seatId}` | Manager TOTP | Delete a seat (fails if bookings exist) |

### Reportees (Members)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/teams/{teamId}/reportees` | — | List team members |
| `POST` | `/api/teams/{teamId}/reportees` | — | Join a team (with TOTP setup, pending approval) |
| `PUT` | `/api/teams/{teamId}/reportees/{id}/approve` | Manager TOTP | Approve a pending member |
| `DELETE` | `/api/teams/{teamId}/reportees/{id}/deny` | Manager TOTP | Deny a pending join request |
| `DELETE` | `/api/teams/{teamId}/reportees/{id}` | Manager TOTP | Remove an approved member (cancels bookings, triggers promotions) |

### Bookings

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/bookings/availability/{teamId}?date=YYYY-MM-DD` | — | View availability, bookings & waitlist |
| `POST` | `/api/bookings` | Reportee TOTP | Book a seat (or waitlist if all seats full) |
| `DELETE` | `/api/bookings/{id}` | Reportee TOTP | Cancel a booking (triggers auto-promotion if confirmed) |

### Health

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/health` | — | Health check |

---

## Workflows

### Overall Flow

```
┌──────────────────────────────────────────────────────────────────────┐
│                          OfficeAschi                                  │
│                                                                      │
│  ①  ┌──────────┐    ┌──────────────┐    ┌───────────────────────┐    │
│     │  Anyone   │───▶│ Create Team  │───▶│ Manager (with TOTP)   │    │
│     └──────────┘    └──────────────┘    └───────┬───────────────┘    │
│          │                                      │                    │
│  ②       │          ┌──────────────┐       ┌────▼────┐               │
│          ├─────────▶│  Join Team   │──────▶│ Pending │               │
│          │          └──────────────┘       └────┬────┘               │
│          │                                      │                    │
│  ③       │                              ┌───────▼────────┐           │
│          │                              │ Manager reviews │           │
│          │                              └───┬────────┬───┘           │
│          │                             Approve     Deny              │
│          │                                │          │               │
│          │                          ┌─────▼──┐   ┌──▼───┐           │
│          │                          │Approved│   │Removed│           │
│          │                          └────┬───┘   └──────┘            │
│          │                               │                           │
│  ④       │    ┌──────────────────────┐   │                           │
│          │    │  View Availability   │◀──┤                           │
│          │    └──────────────────────┘   │                           │
│          │                               ▼                           │
│  ⑤       │                        ┌─────────────┐                    │
│          │                        │  Book Seat   │                    │
│          │                        └──────┬──────┘                    │
│          │                          ┌────┴────┐                      │
│          │                     Seat free?   All full?                │
│          │                          │          │                     │
│          │                    ┌─────▼──┐  ┌───▼──────┐               │
│  ⑥       │                   │Confirmed│  │Waitlisted│               │
│          │                    └────┬───┘  └───┬──────┘               │
│          │                         │          │                      │
│  ⑦       │                    Cancel?    Auto-promoted               │
│          │                         │     when seat freed             │
│          │                         ▼          │                      │
│          │                   ┌──────────┐     │                      │
│          └──────────────────▶│Seat freed│◀────┘                      │
│                              └──────────┘                            │
└──────────────────────────────────────────────────────────────────────┘

  ① Create Team    — anyone creates a team + sets up manager TOTP
  ② Join Team      — anyone joins a team + sets up member TOTP (pending)
  ③ Approve / Deny — manager reviews pending members
  ④ View           — anyone checks seat availability for a date
  ⑤ Book Seat      — approved member books a specific seat
  ⑥ Waitlist       — if all seats full, member waitlists for a seat
  ⑦ Cancel         — cancelling a confirmed booking auto-promotes waitlist
```

---

### 1. Create a Team (Manager)

The home page shows a grid of teams with a "Create Team" button. Clicking it opens a dialog.

```
┌─────────────────────────────────────────┐
│           Create Team Dialog            │
├─────────────────────────────────────────┤
│                                         │
│  Team Name: [ design-squad           ]  │
│                                         │
│  "Scan this QR code with your           │
│   authenticator app"                    │
│                                         │
│         ┌─────────────┐                 │
│         │ ░░░█░░█░░░░ │                 │
│         │ █░░░██░░█░░ │  ◄── QR code    │
│         │ ░░██░░░█░░░ │   (otpauth URI) │
│         │ █░░░░█░░██░ │                 │
│         └─────────────┘                 │
│                                         │
│  Secret: [JBSWY3DPEHPK3PXP]            │
│  [Download QR]  [Copy Secret]           │
│                                         │
│  Verify code: [ 1 2 3 4 5 6 ]          │
│                                         │
│        [Cancel]  [Create Team]          │
└─────────────────────────────────────────┘

What happens behind the scenes:

  Browser                              API
    │                                   │
    │ ① generateSecret()               │
    │    (otpauth library, client-side) │
    │    ──▶ secret = "JBSWY..."       │
    │                                   │
    │ ② Show QR + secret to user       │
    │    User scans with authenticator  │
    │                                   │
    │ ③ User enters 6-digit code       │
    │    from authenticator app         │
    │                                   │
    │ ④ POST /api/teams                │
    │    { name, secretKey, totpCode }  │
    │    ──────────────────────────────▶│
    │                                   │ ⑤ Validate code against secret
    │                                   │   ✓ Create team + store secret
    │           201 Created             │
    │    ◀──────────────────────────────│
    │                                   │
    │ ⑥ Store secret in localStorage   │
    │    totp_manager_{teamId}          │
    │                                   │
```

---

### 2. Add Seats (Manager)

On the team detail page → Manage tab, the manager types a seat label and clicks "Add."

```
┌─────────────────────────────────────────┐
│  Manage > Seats                         │
├─────────────────────────────────────────┤
│                                         │
│  [Desk A ✕]  [Desk B ✕]  [Window-1 ✕]  │
│                                         │
│  Add seat: [ S4              ] [+ Add]  │
│                                         │
└─────────────────────────────────────────┘

  Browser                                  API
    │                                       │
    │ ① User types "S4", clicks [+ Add]    │
    │                                       │
    │ ② Interceptor reads secret from      │
    │    localStorage, generates TOTP code  │
    │                                       │
    │ ③ POST /api/teams/1/seats            │
    │    { "label": "S4" }                  │
    │    Authorization: TOTP manager:1:code │
    │    ───────────────────────────────────▶│
    │                                       │ ④ Validate TOTP
    │                                       │   ✓ Create seat
    │           201 Created                 │
    │    ◀───────────────────────────────────│
    │                                       │
    │ ⑤ Toast: "Seat S4 added"             │
    │    Refresh seat list                  │
```

If the secret is not in localStorage (different browser), the interceptor opens a TOTP prompt dialog instead:

```
  ② (alternate) No secret in localStorage:

  ┌──────────────────────────────────┐
  │       TOTP Code Required         │
  ├──────────────────────────────────┤
  │  🔒 Enter the 6-digit TOTP      │
  │  code for design-squad to        │
  │  add seat.                       │
  │                                  │
  │  TOTP Code: [ _ _ _ _ _ _ ]     │
  │                                  │
  │     [Cancel]   [ADD SEAT]        │
  └──────────────────────────────────┘

  ③ User enters code manually → request sent with that code
```

---

### 3. Join a Team (Reportee)

On the team detail page, a visitor clicks "Join Team." The dialog generates a new TOTP secret for the member.

```
┌─────────────────────────────────────────┐
│        Join design-squad                │
├─────────────────────────────────────────┤
│                                         │
│  Your Friendly Name: [ Alice         ]  │
│                                         │
│  "Scan this QR code with your           │
│   authenticator app"                    │
│                                         │
│         ┌─────────────┐                 │
│         │ ░░█░██░░░█░ │                 │
│         │ █░░░░█░█░░░ │  ◄── QR code    │
│         │ ░█░░█░░░██░ │   (separate     │
│         │ ░░██░█░░░█░ │    from manager)│
│         └─────────────┘                 │
│                                         │
│  Secret: [KZQW6YLMN5XW2ZDB]            │
│  [Download QR]  [Copy Secret]           │
│                                         │
│  Verify code: [ 7 8 9 0 1 2 ]          │
│                                         │
│         [Cancel]  [Join Team]           │
└─────────────────────────────────────────┘

  Browser                                    API
    │                                         │
    │ ① generateSecret() (client-side)       │
    │    Show QR code + secret               │
    │                                         │
    │ ② User scans QR with authenticator     │
    │                                         │
    │ ③ User enters name + 6-digit code      │
    │    Clicks [Join Team]                   │
    │                                         │
    │ ④ POST /api/teams/1/reportees          │
    │    { friendlyName: "Alice",             │
    │      secretKey: "KZQW6...",             │
    │      totpCode: "789012" }               │
    │    ────────────────────────────────────▶ │
    │                                         │ ⑤ Validate code against secret
    │                                         │   ✓ Create reportee (pending)
    │                                         │   Store TOTP secret in DB
    │           201 Created                   │
    │    ◀──────────────────────────────────── │
    │                                         │
    │ ⑥ Store secret: totp_reportee_{id}     │
    │    Store identity: reportee_{teamId}    │
    │    Toast: "Joined as Alice!"            │
    │                                         │
    │ ⚠ Status = PENDING (cannot book yet)    │
```

---

### 4. Approve or Deny Members (Manager)

Pending members appear in the Manage tab. The manager can approve or deny each one.

```
┌─────────────────────────────────────────┐
│  Manage > Pending Approvals             │
├─────────────────────────────────────────┤
│                                         │
│  🟠 Alice        [Approve] [Deny]       │
│  🟠 Bob          [Approve] [Deny]       │
│                                         │
└─────────────────────────────────────────┘

  Approve flow:
  ─────────────
    ① Manager clicks [Approve] on Alice
    ② Interceptor auto-attaches TOTP header
    ③ PUT /api/teams/1/reportees/1/approve
       Authorization: TOTP manager:1:...
    ④ API validates TOTP → sets IsApproved = true
    ⑤ 200 OK → Toast: "Reportee approved"

  Deny flow:
  ──────────
    ① Manager clicks [Deny] on Alice
    ② Confirm dialog: "Deny Alice's request to join?"
    ③ Manager clicks [Yes, Deny]
    ④ Interceptor auto-attaches TOTP header
    ⑤ DELETE /api/teams/1/reportees/1/deny
       Authorization: TOTP manager:1:...
    ⑥ API validates TOTP → deletes reportee record
    ⑦ 200 OK → Toast: "Denied Alice's join request"
```

---

### 5. Book a Seat (Reportee)

The Bookings tab shows a date picker and a seat grid. Available seats show a "Book" button.

```
┌──────────────────────────────────────────────────┐
│           ◀  11  May 2026  ▶   [Today]           │
│           Sunday                                  │
│  🔵 2 booked  🟢 1 available  🟠 0 waitlisted    │
├──────────────────────────────────────────────────┤
│                                                   │
│  ┌──────────────┐ ┌──────────────┐ ┌────────────┐│
│  │ Desk A     ✕ │ │ Desk B     ✕ │ │ Window-1   ││
│  │              │ │              │ │            ││
│  │  👤 Alice    │ │  👤 Bob      │ │  [+ Book]  ││
│  └──────────────┘ └──────────────┘ └────────────┘│
│                                                   │
└──────────────────────────────────────────────────┘

Clicking [+ Book] on an available seat:

  If user has a stored reportee identity (localStorage):
  ┌──────────────────────────────────┐
  │       Book Window-1              │
  ├──────────────────────────────────┤
  │  Book Alice on Window-1 for     │
  │  2026-05-11?                    │
  │                                  │
  │   [Cancel]  [Confirm Booking]    │
  └──────────────────────────────────┘

  If user has NO stored identity (manager or visitor):
  ┌──────────────────────────────────┐
  │       Book Window-1              │
  ├──────────────────────────────────┤
  │  Select a member to assign to   │
  │  Window-1 for 2026-05-11.       │
  │                                  │
  │  Member: [ Alice           ▾ ]  │
  │                                  │
  │     [Cancel]  [Book Seat]        │
  └──────────────────────────────────┘

  Browser                                    API
    │                                         │
    │ ① User clicks [+ Book] on Window-1     │
    │                                         │
    │ ② Dialog opens (self-book or picker)    │
    │    User confirms booking               │
    │                                         │
    │ ③ Interceptor reads reportee secret    │
    │    from localStorage, generates code   │
    │                                         │
    │ ④ POST /api/bookings                   │
    │    { reporteeId: 1, seatId: 3,          │
    │      date: "2026-05-11" }               │
    │    Authorization: TOTP reportee:1:...   │
    │    ────────────────────────────────────▶ │
    │                                         │ ⑤ Validate TOTP
    │                                         │   Check: is reportee approved?
    │                                         │   Check: seat free?
    │                                         │   ✓ Create booking (Confirmed)
    │         201 { status: "Confirmed" }     │
    │    ◀──────────────────────────────────── │
    │                                         │
    │ ⑥ Toast: "Booked Alice on Window-1"    │
    │    Refresh availability view            │
```

---

### 6. Waitlisting (When All Seats Are Full)

When every seat is booked, the available seats section disappears and a waitlist panel appears showing all booked seats with "Wait for it" buttons.

```
┌──────────────────────────────────────────────────┐
│           ◀  12  May 2026  ▶   [Today]           │
│           Monday                                  │
│  🔵 3 booked  🟢 0 available  🟠 1 waitlisted    │
├──────────────────────────────────────────────────┤
│                                                   │
│  ┌──────────────┐ ┌──────────────┐ ┌────────────┐│
│  │ Desk A     ✕ │ │ Desk B     ✕ │ │ Window-1 ✕ ││
│  │  👤 Alice    │ │  👤 Bob      │ │  👤 Carol  ││
│  └──────────────┘ └──────────────┘ └────────────┘│
│                                                   │
│  ┌──────────────────────────────────────────────┐│
│  │ All seats are booked                         ││
│  │ All seats are taken. You can wait for a      ││
│  │ specific seat.                               ││
│  │                                              ││
│  │ ┌─────────┐ ┌─────────┐ ┌──────────┐        ││
│  │ │ Desk A  │ │ Desk B  │ │ Window-1 │        ││
│  │ │ 👤Alice │ │ 👤Bob   │ │ 👤Carol  │        ││
│  │ │[Wait]   │ │[Wait]   │ │[Wait]    │        ││
│  │ └─────────┘ └─────────┘ └──────────┘        ││
│  └──────────────────────────────────────────────┘│
│                                                   │
│  ┌──────────────────────────────────────────────┐│
│  │ Waitlist                                     ││
│  │ #1 — Dave  waiting for seat Desk A           ││
│  │            since 5/10/26, 2:30 PM    [✕]     ││
│  └──────────────────────────────────────────────┘│
└──────────────────────────────────────────────────┘

  Browser                                    API
    │                                         │
    │ ① User clicks [Wait] on Desk A         │
    │                                         │
    │ ② Dialog confirms waitlist intent       │
    │                                         │
    │ ③ Interceptor attaches TOTP header     │
    │                                         │
    │ ④ POST /api/bookings                   │
    │    { reporteeId: 4, seatId: 1,          │
    │      date: "2026-05-12" }               │
    │    Authorization: TOTP reportee:4:...   │
    │    ────────────────────────────────────▶ │
    │                                         │ ⑤ Validate TOTP
    │                                         │   Seat taken + ALL seats full
    │                                         │   → Create booking (Waitlisted)
    │       201 { status: "Waitlisted" }      │
    │    ◀──────────────────────────────────── │
    │                                         │
    │ ⑥ Toast: "Waitlisted Dave for Desk A"  │
    │    Refresh → Dave appears in waitlist   │
```

**Note:** If the chosen seat is taken but OTHER seats are free, the API rejects at step ⑤ with _"This seat is taken. Other seats are available — pick a different one."_

---

### 7. Cancel Booking + Auto-Promotion

When a confirmed booking is cancelled, the waitlist auto-promotion kicks in.

```
  ① User clicks [✕] on Alice's confirmed booking for Desk A

  ② Confirm dialog:
  ┌──────────────────────────────────┐
  │       Cancel Booking             │
  ├──────────────────────────────────┤
  │  Cancel Alice's booking on       │
  │  Desk A for 2026-05-12?         │
  │                                  │
  │   [No, Keep]  [Yes, Cancel]      │
  └──────────────────────────────────┘

  ③ User clicks [Yes, Cancel]

  Browser                                    API
    │                                         │
    │ ④ Interceptor attaches TOTP header     │
    │                                         │
    │ ⑤ DELETE /api/bookings/1               │
    │    Authorization: TOTP reportee:1:...   │
    │    ────────────────────────────────────▶ │
    │                                         │ ⑥ Validate TOTP
    │                                         │   Delete booking
    │                                         │
    │                          ┌──────────────────────────────────┐
    │                          │ ⑦ Waitlist Promotion Logic:     │
    │                          │                                  │
    │                          │   Desk A is now free.            │
    │                          │                                  │
    │                          │   7a. Anyone waitlisted for      │
    │                          │       Desk A specifically?       │
    │                          │       → YES: Dave (earliest)     │
    │                          │       → Promote Dave to Confirmed│
    │                          │                                  │
    │                          │   7b. If NO one for that seat:   │
    │                          │       → Find globally earliest   │
    │                          │         waitlisted in team/date  │
    │                          │       → Assign them the free seat│
    │                          └──────────────────────────────────┘
    │                                         │
    │           200 OK                        │
    │    ◀──────────────────────────────────── │
    │                                         │
    │ ⑧ Toast: "Cancelled Alice's booking"   │
    │    Refresh → Dave now shows Confirmed   │
```

Full promotion example:

```
  Step A — INITIAL STATE:
  ┌────────┬──────────┬──────────┐
  │ Desk A │  Desk B  │ Window-1 │
  │ Alice  │  Bob     │  Carol   │
  │Confirm │ Confirm  │ Confirm  │
  └────────┴──────────┴──────────┘
  Waitlist: #1 Dave→Desk A, #2 Eve→Desk A, #3 Frank→Desk B

  Step B — ALICE CANCELS DESK A:
  ┌────────┬──────────┬──────────┐
  │ Desk A │  Desk B  │ Window-1 │
  │ Dave ✓ │  Bob     │  Carol   │  ← #1 Dave promoted (waitlisted for Desk A)
  │Confirm │ Confirm  │ Confirm  │
  └────────┴──────────┴──────────┘
  Waitlist: #2 Eve→Desk A, #3 Frank→Desk B

  Step C — CAROL CANCELS WINDOW-1:
  ┌────────┬──────────┬──────────┐
  │ Desk A │  Desk B  │ Window-1 │
  │ Dave   │  Bob     │  Eve ✓   │  ← #2 Eve promoted (globally earliest,
  │Confirm │ Confirm  │ Confirm  │     moved from Desk A waitlist to Window-1)
  └────────┴──────────┴──────────┘
  Waitlist: #3 Frank→Desk B
```

---

### 8. Remove a Member (Manager)

From the Manage tab, the manager can remove an approved member. This cascades — all their bookings are cancelled and waitlist promotions fire for each vacated seat.

```
┌─────────────────────────────────────────┐
│  Manage > Members                       │
├─────────────────────────────────────────┤
│  👤 Alice                        [🗑]   │
│  👤 Bob                          [🗑]   │
│  👤 Carol                        [🗑]   │
└─────────────────────────────────────────┘

  ① Manager clicks [🗑] on Alice

  ② Confirm dialog:
  ┌──────────────────────────────────┐
  │       Remove Member              │
  ├──────────────────────────────────┤
  │  Remove Alice from this team?    │
  │  All their bookings will be      │
  │  cancelled.                      │
  │                                  │
  │    [No, Keep]  [Yes, Remove]     │
  └──────────────────────────────────┘

  ③ Manager clicks [Yes, Remove]

  Browser                                    API
    │                                         │
    │ ④ Interceptor attaches TOTP header     │
    │                                         │
    │ ⑤ DELETE /api/teams/1/reportees/1      │
    │    Authorization: TOTP manager:1:...    │
    │    ────────────────────────────────────▶ │
    │                                         │ ⑥ Validate TOTP
    │                                         │ ⑦ Cancel all Alice's bookings
    │                                         │ ⑧ For each confirmed seat vacated:
    │                                         │      → run waitlist promotion
    │                                         │ ⑨ Delete Alice's reportee record
    │           200 OK                        │
    │    ◀──────────────────────────────────── │
    │                                         │
    │ ⑩ Toast: "Removed Alice from team"     │
    │    Refresh member list + availability   │
```

---

### 9. Delete a Seat (Manager)

From the Manage tab, seats show an ✕ button. Deletion only works if the seat has zero bookings.

```
  ┌──────────────────────────────────────────┐
  │  Seats: [Desk A ✕] [Desk B ✕] [S4 ✕]   │
  └──────────────────────────────────────────┘

  ① Manager clicks ✕ on S4
  ② Interceptor attaches TOTP header
  ③ DELETE /api/teams/1/seats/{seatId}
     Authorization: TOTP manager:1:...

  ④ API checks:
     ├── Has bookings?
     │   → YES: 400 "Cannot delete seat with existing
     │          bookings. Cancel all bookings first."
     │
     └── NO bookings?
         → ⑤ Delete seat
         → ⑥ 200 OK → Toast: "Seat S4 deleted"
```

---

### 10. Delete a Team (Manager)

In the Manage tab's Danger Zone, the manager can permanently delete the entire team.

```
┌─────────────────────────────────────────┐
│  Danger Zone                            │
│  Permanently delete this team and all   │
│  its members, seats, and bookings.      │
│                                         │
│  [Delete Team]                          │
└─────────────────────────────────────────┘

  ① Manager clicks [Delete Team]

  ② Confirm dialog:
  ┌──────────────────────────────────┐
  │       Delete Team                │
  ├──────────────────────────────────┤
  │  Delete design-squad?            │
  │  All members, seats, and         │
  │  bookings will be permanently    │
  │  removed.                        │
  │                                  │
  │    [No, Keep]  [Yes, Delete]     │
  └──────────────────────────────────┘

  ③ Manager clicks [Yes, Delete]

  Browser                                    API
    │                                         │
    │ ④ Interceptor attaches TOTP header     │
    │                                         │
    │ ⑤ DELETE /api/teams/1                  │
    │    Authorization: TOTP manager:1:...    │
    │    ────────────────────────────────────▶ │
    │                                         │ ⑥ Validate TOTP
    │                                         │ ⑦ Delete all bookings
    │                                         │ ⑧ Delete all reportees
    │                                         │ ⑨ Delete all seats
    │                                         │ ⑩ Delete team record
    │           200 OK                        │
    │    ◀──────────────────────────────────── │
    │                                         │
    │ ⑪ Toast: "Team design-squad deleted"   │
    │    Navigate → Home page                 │
```

---

### 11. TOTP Interceptor Flow (Every Authenticated Request)

This is the invisible magic that makes auth seamless in the UI. The HTTP interceptor runs on every API call that carries TOTP context.

```
  ┌────────────────────────────────────────────────────────────┐
  │                     HTTP Interceptor                        │
  │                                                            │
  │  ① Request has TOTP context?                               │
  │     (entity type + entity id on HttpContext)               │
  │     │                                                      │
  │     ├── NO ──▶ ② Pass through (public endpoint)           │
  │     │                                                      │
  │     └── YES                                                │
  │          │                                                 │
  │     ③ Secret in localStorage?                              │
  │          │                                                 │
  │          ├── YES                                           │
  │          │    │                                            │
  │          │    ④ Generate 6-digit code from secret          │
  │          │    ⑤ Attach: Authorization: TOTP type:id:code  │
  │          │    ⑥ Send request ──▶ API                      │
  │          │                                                 │
  │          └── NO                                            │
  │               │                                            │
  │               ④ Open TOTP Prompt Dialog:                   │
  │               ┌─────────────────────────┐                  │
  │               │  🔒 TOTP Code Required  │                  │
  │               │  "Enter 6-digit code    │                  │
  │               │   for {name} to         │                  │
  │               │   {action reason}"      │                  │
  │               │                         │                  │
  │               │  [ _ _ _ _ _ _ ]        │                  │
  │               │  [Cancel] [Authorize]   │                  │
  │               └──────────┬──────────────┘                  │
  │                          │                                 │
  │               ⑤ User enters code → Attach header           │
  │               ⑥ Send request ──▶ API                       │
  │               (or Cancel → request aborted, returns EMPTY) │
  └────────────────────────────────────────────────────────────┘
```

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| **Backend** | |
| Runtime | .NET 10 |
| Framework | ASP.NET Core Web API |
| Database | SQLite (via EF Core) |
| Auth | TOTP (Otp.NET) — custom middleware + `[TotpAuth]` attribute |
| API Docs | Swagger / Swashbuckle |
| **Frontend** | |
| Framework | Angular 21 (standalone components) |
| UI Library | Hyland UI (`@hyland/ui` + `@hyland/ui-shell`) |
| Material | Angular Material 21 |
| TOTP (client) | `otpauth` (secret generation + code generation + validation) |
| QR Codes | `qrcode` (branded QR with logo) |
| Input Masking | `angular-imask` (6-digit TOTP input) |
| i18n | Transloco (`@jsverse/transloco`) — English, Bengali, Hindi |
| PWA | Angular Service Worker (`@angular/service-worker`) |
| Mobile | Capacitor (`@capacitor/android`) — native Android builds |
| State | Angular Signals |

---

## Running Locally

```bash
# Prerequisites: .NET 10 SDK, Node.js

# Backend — clone and run
cd OfficeAschiApi
dotnet run

# Swagger UI
open http://localhost:5079/swagger

# Frontend — in a separate terminal
cd ClientApp
npm install
npm start
# App runs at http://localhost:4200 (proxies API to backend)
```

The SQLite database (`officeaschi.db`) is auto-created on first run.

For production, the Angular app is built and served as static files from the .NET backend (`wwwroot` + fallback to `index.html` for client-side routing).

---

## Project Structure

```
OfficeAschiApi/
├── Controllers/
│   ├── TeamsController.cs         # Create/delete team (atomic TOTP setup)
│   ├── SeatsController.cs         # Add/delete seats (manager auth)
│   ├── ReporteesController.cs     # Join/approve/deny/remove members
│   └── BookingsController.cs      # Book/cancel + availability view
├── Data/
│   └── AppDbContext.cs            # EF Core context + schema constraints
├── DTOs/
│   └── DTOs.cs                    # Request/response records
├── Filters/
│   └── TotpSecurityOperationFilter.cs  # Swagger TOTP security docs
├── Middleware/
│   └── TotpAuthMiddleware.cs      # TOTP auth check + [TotpAuth] attribute
├── Models/
│   ├── Team.cs                    # Team + ManagerTotpSecret
│   ├── Seat.cs                    # Seat label + team FK
│   ├── Reportee.cs                # Member + TotpSecret + approval state
│   └── Booking.cs                 # Booking + status (Confirmed/Waitlisted)
├── Services/
│   ├── TotpService.cs             # TOTP validation (Otp.NET)
│   └── WaitlistService.cs         # Auto-promotion on cancellation
├── Program.cs                     # App wiring, Swagger, middleware, static files
│
├── ClientApp/                     # Angular 21 PWA
│   ├── src/app/
│   │   ├── app.component.ts       # Shell layout + backend health banner
│   │   ├── app.config.ts          # Providers, router, interceptors, i18n, PWA
│   │   ├── app.routes.ts          # / (search), /team/:id (detail), /dev (dev-only)
│   │   ├── models.ts              # TypeScript interfaces matching API DTOs
│   │   ├── services/
│   │   │   ├── booking.service.ts # API client (HttpClient + TOTP context tokens)
│   │   │   ├── download.service.ts # QR download (browser + Capacitor native)
│   │   │   └── noop-auth.service.ts # Stub for Hyland UI auth requirement
│   │   ├── totp/
│   │   │   ├── totp.service.ts    # Client-side TOTP: generate secret, generate code, validate, localStorage
│   │   │   ├── totp.interceptor.ts # HTTP interceptor: auto-attach TOTP header or prompt
│   │   │   ├── totp.context.ts    # HttpContext tokens for entity type/id/name/reason
│   │   │   └── totp-code-input.component.ts  # Masked 6-digit input
│   │   ├── team-search/           # Home page: search teams, create team
│   │   ├── team-detail/           # Team view: bookings tab + manage tab
│   │   ├── dashboard/             # Redirects to /
│   │   ├── dialogs/
│   │   │   ├── team-create-dialog.component.ts   # Create team + QR + TOTP verify
│   │   │   ├── join-team-dialog.component.ts      # Join team + QR + TOTP verify
│   │   │   ├── book-seat-dialog.component.ts      # Book seat (self or pick member)
│   │   │   ├── cancel-book-confirm-dialog.component.ts  # Confirm destructive actions
│   │   │   ├── totp-prompt-dialog.component.ts    # Manual TOTP code entry fallback
│   │   │   └── reportee-picker-dialog.component.ts # Select member from list
│   │   └── dev-menu/              # Dev-only: implant/view/remove TOTP secrets
│   ├── public/
│   │   ├── i18n/                  # Translation files (en, bn, hi)
│   │   ├── icons/                 # PWA icons
│   │   └── manifest.webmanifest   # PWA manifest
│   └── android/                   # Capacitor Android project
└── README.md
```
