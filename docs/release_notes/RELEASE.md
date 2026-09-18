# Pia next

## Assistant

- Long conversations are cheaper and faster from the second turn on, on
  providers that cache a prompt prefix. The saving grows with the chat, so it is
  largest exactly where a reply used to feel slowest.
- Every reply starts sooner. Pia paced its own requests half a second apart even
  when nothing had asked it to wait; that gap is now a tenth of a second. Agent
  runs feel it most, because they pay it on every round of thinking.
- Agent runs stop thinking once they have what they need. Building a plan and
  checking the finished work each spent one extra round on an answer nothing
  read — about nine seconds a run.
