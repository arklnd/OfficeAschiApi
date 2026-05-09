# OfficeAschi API

**Office seat booking system with TOTP-based authentication.**

A lightweight REST API for teams to manage daily office seat bookings — with automatic waitlist promotion when seats free up.

---

## The Idea

In a hybrid workplace, teams need a simple way to coordinate who's coming to the office and which seat they'll use. **OfficeAschi** solves this:

- A **Manager** creates a team and defines available seats
- **Reportees** (team members) join the team and book seats for specific days
- When all seats are full, members can **waitlist** for a specific seat
- If a booking is cancelled, the **next waitlisted person is automatically promoted**
- All elevated actions (adding seats, booking, cancelling) are secured via **TOTP** (Time-based One-Time Password) — no passwords, no sessions, no tokens to manage

---

## Core Concepts

### Roles

| Role | Can Do |
|------|--------|
| **Manager** | Create team, set up TOTP, add seats, approve reportees |
| **Reportee** | Join a team, set up TOTP, book/cancel seats (after approval) |
| **Anyone** | Search teams, view seats, check availability |

### Authentication: TOTP

Instead of traditional username/password auth, each actor (manager or reportee) sets up a **TOTP secret** (like Google Authenticator). Every elevated API call requires a valid 6-digit TOTP code in the `Authorization` header:

```
Authorization: TOTP manager:{teamId}:{6-digit-code}
Authorization: TOTP reportee:{reporteeId}:{6-digit-code}
```

TOTP setup is a two-step process:
1. Generate a secret key (`GET /api/teams/generate-secret`)
2. Add the secret to an authenticator app, then verify by providing the secret + a valid code to the setup endpoint

### Waitlist Logic

When all seats in a team are booked for a day, a member can still book — but their booking is **waitlisted** for their desired seat.

**Auto-promotion on cancellation:**
1. When a confirmed booking is cancelled, first check if anyone is waitlisted **for that specific seat** → promote the earliest one
2. If no one is waitlisted for that seat, find the **globally earliest waitlisted person** in the team for that day → assign them the vacated seat and remove from their original waitlist

**Example:**
> Team-101 has 3 seats (S1, S2, S3) and 15 members (M1–M15).
>
> **Confirmed:** S1←M1, S2←M2, S3←M3
> **Waitlisted for S1:** M4, M5, M6 (in order)
>
> - M1 cancels → M4 automatically gets S1 (earliest waitlisted for S1)
> - M3 cancels S3 → M5 gets S3 (next globally earliest waitlisted, removed from S1 waitlist)

---

## API Endpoints

### Teams (Public)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/teams?q={search}` | Search/list teams |
| `GET` | `/api/teams/{id}` | Get team details |
| `POST` | `/api/teams` | Create a new team |
| `GET` | `/api/teams/generate-secret` | Generate a TOTP secret key |
| `POST` | `/api/teams/{id}/setup-totp` | Set up TOTP for the team manager |

### Seats

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/teams/{teamId}/seats` | — | List seats in a team |
| `POST` | `/api/teams/{teamId}/seats` | Manager TOTP | Add a seat to the team |

### Reportees

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/teams/{teamId}/reportees` | — | List team members |
| `POST` | `/api/teams/{teamId}/reportees` | — | Join a team (pending approval) |
| `POST` | `/api/teams/{teamId}/reportees/{id}/setup-totp` | — | Set up TOTP for a reportee |
| `PUT` | `/api/teams/{teamId}/reportees/{id}/approve` | Manager TOTP | Approve a reportee |

### Bookings

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/bookings/availability/{teamId}?date=YYYY-MM-DD` | — | View availability, bookings & waitlist |
| `POST` | `/api/bookings` | Reportee TOTP | Book a seat (or waitlist if full) |
| `DELETE` | `/api/bookings/{id}` | Reportee TOTP | Cancel a booking (triggers auto-promotion) |

---

## Typical Workflow

```
1. Manager creates a team
   POST /api/teams  { "name": "design-squad" }

2. Manager sets up TOTP
   GET  /api/teams/generate-secret  → get secretKey
   POST /api/teams/1/setup-totp     { "secretKey": "...", "totpCode": "123456" }

3. Manager adds seats (TOTP required)
   POST /api/teams/1/seats  { "label": "Desk A" }
   Authorization: TOTP manager:1:654321

4. Reportee joins the team
   POST /api/teams/1/reportees  { "friendlyName": "Alice" }

5. Reportee sets up TOTP
   GET  /api/teams/generate-secret
   POST /api/teams/1/reportees/1/setup-totp  { "secretKey": "...", "totpCode": "789012" }

6. Manager approves the reportee (TOTP required)
   PUT  /api/teams/1/reportees/1/approve
   Authorization: TOTP manager:1:654321

7. Reportee books a seat (TOTP required)
   POST /api/bookings  { "reporteeId": 1, "seatId": 1, "date": "2026-05-12" }
   Authorization: TOTP reportee:1:789012

8. Check availability (public)
   GET /api/bookings/availability/1?date=2026-05-12
```

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10 |
| Framework | ASP.NET Core Web API |
| Database | SQLite (via EF Core) |
| Auth | TOTP (Otp.NET) — custom middleware |
| API Docs | Swagger / Swashbuckle |

---

## Running Locally

```bash
# Prerequisites: .NET 10 SDK

# Clone and run
cd OfficeAschiApi
dotnet run

# Swagger UI
open http://localhost:5079/swagger
```

The SQLite database (`officeaschi.db`) is auto-created on first run.

---

## Project Structure

```
OfficeAschiApi/
├── Controllers/
│   ├── TeamsController.cs        # Team CRUD + TOTP setup
│   ├── SeatsController.cs        # Seat management
│   ├── ReporteesController.cs    # Join/approve team members
│   └── BookingsController.cs     # Book/cancel + availability
├── Data/
│   └── AppDbContext.cs           # EF Core context + schema
├── DTOs/
│   └── DTOs.cs                   # Request/response records
├── Middleware/
│   └── TotpAuthMiddleware.cs     # TOTP auth check + [TotpAuth] attribute
├── Models/
│   ├── Team.cs
│   ├── Seat.cs
│   ├── Reportee.cs
│   └── Booking.cs
├── Services/
│   ├── TotpService.cs            # TOTP validation & secret generation
│   └── WaitlistService.cs        # Auto-promotion on cancellation
└── Program.cs                    # App wiring, Swagger, middleware
```
