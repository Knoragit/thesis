//! Receiving commands from Unity.

use std::net::{SocketAddr, UdpSocket};
use std::thread;

use super::services::{Clock, Wake};
use crate::core::Input;
use crate::protocol::parse_command;

/// Binds the command socket. Fails if another bridge already holds the port.
pub fn bind(address: SocketAddr) -> std::io::Result<UdpSocket> {
    UdpSocket::bind(address)
}

/// Forwards every valid command to the event loop until the socket fails.
pub fn spawn_receiver(socket: UdpSocket, wakes: std::sync::mpsc::Sender<Wake>, clock: Clock) {
    thread::spawn(move || {
        let mut buffer = vec![0u8; 65_536];
        loop {
            let len = match socket.recv_from(&mut buffer) {
                Ok((len, _)) => len,
                Err(error) => {
                    tracing::error!(%error, "command socket failed");
                    return;
                }
            };
            if let Some(command) = parse_command(&buffer[..len]) {
                tracing::debug!(?command, "from Unity");
                let _ = wakes.send(Wake {
                    input: Input::Command(command),
                    at: clock.now(),
                });
            } else {
                tracing::debug!(
                    datagram = %String::from_utf8_lossy(&buffer[..len]),
                    "ignored datagram"
                );
            }
        }
    });
}
